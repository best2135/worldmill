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
        /// <summary>UV0.x = biome ID (0..5), UV0.y = GPU-baked SDF ambient occlusion (0..1).</summary>
        public Vector2[] BiomeAndAo;
        public int[] Indices;

        public bool IsEmpty => Positions == null || Positions.Length == 0 || Indices == null || Indices.Length == 0;
        public int TriangleCount => Indices == null ? 0 : Indices.Length / 3;
    }

    /// <summary>
    /// Decodes WorldMill WML1 binary frames from the Rust + headless wgpu backend and converts
    /// right-handed Y-up coordinates into Unity's left-handed Y-up coordinate space.
    /// </summary>
    public static class WmlProtocol
    {
        private const int HeaderBytes = 40;
        private const int VertexStrideBytes = 32;
        private const uint Wml1Magic = 0x314C4D57u; // "WML1" little-endian

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
                BiomeAndAo = new Vector2[vertexCount],
                Indices = new int[indexCount],
            };

            // Convert Rust right-handed (X, Y, Z) -> Unity left-handed (X, Y, -Z)
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

                chunk.Positions[i] = new Vector3(px, py, -pz);
                chunk.Normals[i] = new Vector3(nx, ny, -nz).normalized;
                chunk.BiomeAndAo[i] = new Vector2(biome, Mathf.Clamp(ao, 0.15f, 1.0f));
                vOff += VertexStrideBytes;
            }

            // Mirroring Z maps RH CCW onto LH CCW; Unity expects LH CW for front faces,
            // so swap (i1, i2) on every triangle to keep normals & shadows strictly outward.
            int iOff = HeaderBytes + vertexCount * VertexStrideBytes;
            for (int t = 0; t + 2 < indexCount; t += 3)
            {
                chunk.Indices[t + 0] = (int)BitConverter.ToUInt32(data, iOff + (t + 0) * 4);
                chunk.Indices[t + 1] = (int)BitConverter.ToUInt32(data, iOff + (t + 2) * 4);
                chunk.Indices[t + 2] = (int)BitConverter.ToUInt32(data, iOff + (t + 1) * 4);
            }

            return chunk;
        }
    }
}
