using System;
using UnityEngine;

namespace WorldMill
{
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int Cx;
        public readonly int Cy;
        public readonly int Cz;
        public readonly uint Lod;

        public ChunkCoord(int cx, int cy, int cz, uint lod)
        {
            Cx = cx;
            Cy = cy;
            Cz = cz;
            Lod = lod;
        }

        public bool Equals(ChunkCoord other) =>
            Cx == other.Cx && Cy == other.Cy && Cz == other.Cz && Lod == other.Lod;

        public override bool Equals(object obj) => obj is ChunkCoord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Cx, Cy, Cz, Lod);

        public override string ToString() => $"L{Lod}({Cx},{Cy},{Cz})";
    }

    public sealed class DecodedChunk
    {
        public ChunkCoord Coord;
        public uint Seed;
        public uint BakeUs;
        public bool CacheHit;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] UVs;
        /// <summary>
        /// Vertex color mask for HDRP/LayeredLit (_VertexColorMode = Multiply):
        /// Layer 0 (Base) = Granite/Basalt Rock (when R=G=B=0)
        /// R = Layer 1 (Alpine Meadow Grass)
        /// G = Layer 2 (Warm Canyon Sandstone / Shore)
        /// B = Layer 3 (Alpine Snow / Crystal)
        /// A = GPU-baked SDF Ambient Occlusion
        /// </summary>
        public Color[] LayerColors;
        public int[] Indices;

        public bool IsEmpty => Positions == null || Positions.Length == 0 || Indices == null || Indices.Length == 0;
        public int TriangleCount => Indices == null ? 0 : Indices.Length / 3;
    }

    public static class WmlProtocol
    {
        private const int HeaderBytes = 40;
        private const int VertexStrideBytes = 32;
        private const uint Wml1Magic = 0x314C4D57u; // "WML1"

        public static DecodedChunk Decode(byte[] data, int count)
        {
            if (count < HeaderBytes)
                throw new InvalidOperationException($"WML1 packet too short: {count} bytes");

            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != Wml1Magic)
                throw new InvalidOperationException($"Invalid WML1 magic: 0x{magic:X8}");

            int cx = BitConverter.ToInt32(data, 4);
            int cy = BitConverter.ToInt32(data, 8);
            int cz = BitConverter.ToInt32(data, 12);
            uint lod = BitConverter.ToUInt32(data, 16);
            uint seed = BitConverter.ToUInt32(data, 20);
            int vertexCount = (int)BitConverter.ToUInt32(data, 24);
            int indexCount = (int)BitConverter.ToUInt32(data, 28);
            uint bakeUs = BitConverter.ToUInt32(data, 32);
            uint flags = BitConverter.ToUInt32(data, 36);

            int expected = HeaderBytes + vertexCount * VertexStrideBytes + indexCount * 4;
            if (count < expected)
                throw new InvalidOperationException($"Truncated WML1 packet: got {count}, expected {expected}");

            var chunk = new DecodedChunk
            {
                Coord = new ChunkCoord(cx, cy, cz, lod),
                Seed = seed,
                BakeUs = bakeUs,
                CacheHit = (flags & 1u) != 0,
                Positions = new Vector3[vertexCount],
                Normals = new Vector3[vertexCount],
                UVs = new Vector2[vertexCount],
                LayerColors = new Color[vertexCount],
                Indices = new int[indexCount],
            };

            int vOff = HeaderBytes;
            for (int i = 0; i < vertexCount; i++)
            {
                float px = BitConverter.ToSingle(data, vOff + 0);
                float py = BitConverter.ToSingle(data, vOff + 4);
                float pz = BitConverter.ToSingle(data, vOff + 8);
                float ao = BitConverter.ToSingle(data, vOff + 12);
                float nx = BitConverter.ToSingle(data, vOff + 16);
                float ny = BitConverter.ToSingle(data, vOff + 20);
                float nz = BitConverter.ToSingle(data, vOff + 24);
                float biome = BitConverter.ToSingle(data, vOff + 28);

                var pos = new Vector3(px, py, -pz);
                var norm = new Vector3(nx, ny, -nz).normalized;
                chunk.Positions[i] = pos;
                chunk.Normals[i] = norm;
                chunk.UVs[i] = new Vector2(pos.x * 0.1f, pos.z * 0.1f);
                chunk.LayerColors[i] = ComputeLayerMask(biome, pos, norm, ao);
                vOff += VertexStrideBytes;
            }

            // Mirroring Z maps RH CCW -> LH CCW; Unity expects LH CW front faces, so swap (i1, i2).
            int iOff = HeaderBytes + vertexCount * VertexStrideBytes;
            for (int t = 0; t + 2 < indexCount; t += 3)
            {
                chunk.Indices[t + 0] = (int)BitConverter.ToUInt32(data, iOff + (t + 0) * 4);
                chunk.Indices[t + 1] = (int)BitConverter.ToUInt32(data, iOff + (t + 2) * 4);
                chunk.Indices[t + 2] = (int)BitConverter.ToUInt32(data, iOff + (t + 1) * 4);
            }

            return chunk;
        }

        private static Color ComputeLayerMask(float biome, Vector3 pos, Vector3 n, float ao)
        {
            // Smooth slope-aware PBR weights so biomes blend organically via HDRP height-based blending
            float grass = 0f;
            float sand = 0f;
            float snow = 0f;

            int id = Mathf.RoundToInt(biome);
            float flatness = Mathf.SmoothStep(0.48f, 0.80f, n.y);

            if (id == 1)
            {
                // Warm sandstone / shoreline
                sand = Mathf.Lerp(0.45f, 1.0f, flatness);
            }
            else if (id == 2)
            {
                // Lush meadow on flat areas, revealing cliff granite on steeper slopes
                grass = flatness;
            }
            else if (id == 4 || id == 5)
            {
                // Alpine snow / icy crystal caps
                snow = Mathf.Lerp(0.35f, 1.0f, flatness);
            }
            else
            {
                // Biomes 0 (cavern basalt) & 3 (granite cliff) -> Layer 0 base rock!
                // Subtle snow dust on high ledges, subtle sand near water
                if (pos.y > 28f && n.y > 0.65f)
                    snow = Mathf.InverseLerp(28f, 38f, pos.y) * 0.7f;
                else if (pos.y > -8f && pos.y < -2f && n.y > 0.55f)
                    sand = 0.65f;
            }

            return new Color(grass, sand, snow, Mathf.Clamp(ao, 0.15f, 1.0f));
        }
    }
}
