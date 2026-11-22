using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldMill
{
    [Serializable]
    public sealed class ServerStats
    {
        public string adapter;
        public string backend;
        public int worker_slots;
        public int cached_chunks;
        public ulong gpu_bakes;
        public ulong cache_hits;
        public double avg_gpu_ms;
        public ulong total_triangles;
    }

    [Serializable]
    internal sealed class ServerEnvelope
    {
        public string type;
        public string adapter;
        public string backend;
        public int worker_slots;
        public int cached_chunks;
        public ulong gpu_bakes;
        public ulong cache_hits;
        public double avg_gpu_ms;
        public ulong total_triangles;
        public CoordJson[] coords;
    }

    [Serializable]
    internal sealed class CoordJson
    {
        public int cx;
        public int cy;
        public int cz;
        public uint lod;
    }

    /// <summary>
    /// Streams live 3D procedural chunks from the Rust + headless wgpu WorldMill backend
    /// into Unity 6 HDRP with 4-layer Triplanar PBR LayeredLit materials & MeshColliders.
    /// </summary>
    public sealed class WorldMillStreamer : MonoBehaviour
    {
        [Header("Backend Connection")]
        public string wsUrl = "ws://127.0.0.1:8080/ws";
        public uint worldSeed = 42;
        public int nearRadius = 2;
        public int farRadius = 4;

        [Header("HDRP Material & Physics")]
        public Material terrainMaterial;
        public bool generateMeshColliders = true;
        public int maxUploadsPerFrame = 16;

        public bool IsConnected { get; private set; }
        public string ConnectionStatus { get; private set; } = "Connecting...";
        public ServerStats LatestStats { get; private set; }
        public int ActiveChunkCount => _chunkObjects.Count;
        public int ActiveTriangleCount { get; private set; }

        public event Action<ChunkCoord, DecodedChunk> OnChunkSpawned;
        public event Action<ChunkCoord> OnChunkRemoved;
        public event Action OnWorldCleared;

        private readonly Dictionary<ChunkCoord, (GameObject go, Mesh mesh, int tris)> _chunkObjects = new();
        private readonly ConcurrentQueue<DecodedChunk> _incomingChunks = new();
        private readonly ConcurrentQueue<ChunkCoord[]> _incomingEvictions = new();
        private readonly ConcurrentQueue<string> _outgoingJson = new();

        private CancellationTokenSource _cts;
        private Vector3 _lastSentPos = new(99999f, 99999f, 99999f);
        private Vector3 _lastSentDir = Vector3.forward;
        private float _lastSentTime = -10f;
        private Transform _chunkRoot;

        private void Awake()
        {
            _chunkRoot = new GameObject("StreamedChunks").transform;
            _chunkRoot.SetParent(transform, false);
        }

        private void OnEnable()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => WebSocketLoop(_cts.Token));
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        private void OnDestroy()
        {
            ClearAllChunks();
        }

        public void ChangeSeed(uint newSeed)
        {
            worldSeed = newSeed;
            ClearAllChunks();
            _outgoingJson.Enqueue("{\"type\":\"clear_client_cache\"}");
            ForceSendPose();
        }

        public void ForceSendPose()
        {
            _lastSentTime = -10f;
        }

        private void Update()
        {
            SendPoseIfNeeded();
            DrainEvictions();
            DrainChunks();
        }

        private void SendPoseIfNeeded()
        {
            if (!IsConnected || Camera.main == null) return;
            var camTf = Camera.main.transform;
            float now = Time.unscaledTime;
            float moved = Vector3.Distance(camTf.position, _lastSentPos);
            float dot = Vector3.Dot(camTf.forward, _lastSentDir);

            if (now - _lastSentTime < 0.14f) return;
            if (moved < 2.0f && dot > 0.985f && _lastSentTime > 0f) return;

            _lastSentPos = camTf.position;
            _lastSentDir = camTf.forward;
            _lastSentTime = now;

            var p = camTf.position;
            var d = camTf.forward;
            string json = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{{\"type\":\"pose\",\"pos\":[{0:F3},{1:F3},{2:F3}],\"dir\":[{3:F4},{4:F4},{5:F4}],\"seed\":{6},\"near_radius\":{7},\"far_radius\":{8}}}",
                p.x, p.y, -p.z,
                d.x, d.y, -d.z,
                worldSeed, nearRadius, farRadius
            );
            _outgoingJson.Enqueue(json);
        }

        private void DrainEvictions()
        {
            while (_incomingEvictions.TryDequeue(out var coords))
            {
                foreach (var c in coords)
                    RemoveChunk(c);
            }
        }

        private void DrainChunks()
        {
            int uploaded = 0;
            while (uploaded < maxUploadsPerFrame && _incomingChunks.TryDequeue(out var chunk))
            {
                if (chunk.Seed != worldSeed) continue;
                RemoveChunk(chunk.Coord);

                if (chunk.IsEmpty) continue;

                var mesh = new Mesh
                {
                    name = $"Chunk_{chunk.Coord}",
                    indexFormat = IndexFormat.UInt32
                };
                mesh.SetVertices(chunk.Positions);
                mesh.SetNormals(chunk.Normals);
                mesh.SetUVs(0, chunk.UVs);
                mesh.SetColors(chunk.LayerColors);
                mesh.SetIndices(chunk.Indices, MeshTopology.Triangles, 0, true);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();

                var go = new GameObject($"Chunk_{chunk.Coord}");
                go.transform.SetParent(_chunkRoot, false);

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;

                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = terrainMaterial;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;

                if (generateMeshColliders && chunk.Coord.Lod == 0)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                }

                ActiveTriangleCount += chunk.TriangleCount;
                _chunkObjects[chunk.Coord] = (go, mesh, chunk.TriangleCount);
                OnChunkSpawned?.Invoke(chunk.Coord, chunk);
                uploaded++;
            }
        }

        private void RemoveChunk(ChunkCoord coord)
        {
            if (!_chunkObjects.TryGetValue(coord, out var entry)) return;
            ActiveTriangleCount = Math.Max(0, ActiveTriangleCount - entry.tris);
            if (entry.go != null) Destroy(entry.go);
            if (entry.mesh != null) Destroy(entry.mesh);
            _chunkObjects.Remove(coord);
            OnChunkRemoved?.Invoke(coord);
        }

        public void ClearAllChunks()
        {
            foreach (var entry in _chunkObjects.Values)
            {
                if (entry.go != null) Destroy(entry.go);
                if (entry.mesh != null) Destroy(entry.mesh);
            }
            _chunkObjects.Clear();
            ActiveTriangleCount = 0;
            while (_incomingChunks.TryDequeue(out _)) { }
            OnWorldCleared?.Invoke();
        }

        private async Task WebSocketLoop(CancellationToken ct)
        {
            var recvBuf = new byte[256 * 1024];

            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    ConnectionStatus = $"Connecting to {wsUrl}...";
                    await ws.ConnectAsync(new Uri(wsUrl), ct);
                    IsConnected = true;
                    ConnectionStatus = "Connected";
                    _lastSentTime = -10f;

                    var sendTask = Task.Run(async () =>
                    {
                        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            if (_outgoingJson.TryDequeue(out var msg))
                            {
                                var bytes = Encoding.UTF8.GetBytes(msg);
                                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                            }
                            else
                            {
                                await Task.Delay(15, ct);
                            }
                        }
                    }, ct);

                    using var ms = new MemoryStream(256 * 1024);
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(recvBuf, ct);
                            if (result.MessageType == WebSocketMessageType.Close)
                                break;
                            ms.Write(recvBuf, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            var chunk = WmlProtocol.Decode(ms.GetBuffer(), (int)ms.Length);
                            _incomingChunks.Enqueue(chunk);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string txt = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            HandleServerText(txt);
                        }
                    }

                    await Task.WhenAny(sendTask, Task.Delay(100, ct));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ConnectionStatus = $"Disconnected ({ex.Message}) — retrying...";
                }
                finally
                {
                    IsConnected = false;
                }

                try { await Task.Delay(1200, ct); } catch { break; }
            }
        }

        private void HandleServerText(string json)
        {
            var env = JsonUtility.FromJson<ServerEnvelope>(json);
            if (env == null) return;
            if (env.type == "stats")
            {
                LatestStats = new ServerStats
                {
                    adapter = env.adapter,
                    backend = env.backend,
                    worker_slots = env.worker_slots,
                    cached_chunks = env.cached_chunks,
                    gpu_bakes = env.gpu_bakes,
                    cache_hits = env.cache_hits,
                    avg_gpu_ms = env.avg_gpu_ms,
                    total_triangles = env.total_triangles
                };
            }
            else if (env.type == "evict" && env.coords != null && env.coords.Length > 0)
            {
                var list = new ChunkCoord[env.coords.Length];
                for (int i = 0; i < env.coords.Length; i++)
                    list[i] = new ChunkCoord(env.coords[i].cx, env.coords[i].cy, env.coords[i].cz, env.coords[i].lod);
                _incomingEvictions.Enqueue(list);
            }
        }
    }
}
