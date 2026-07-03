// WebGpuWater - procedural water grid builder.
// Runtime (not editor-only) because it serves two callers: the editor build kit bakes
// the authored grid asset from it, and the Low quality tier rebuilds a coarser grid at
// startup on weak devices - the vertex shader runs 4 texture fetches plus the wave-bank
// sines PER VERTEX, so grid density is a first-order cost on mobile GPUs.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterMeshBuilder
    {
        // Generated meshes keep huge bounds so Unity's renderer culling can never wrongly
        // cull a surface placed by the volume frame; real frustum culling is
        // WaterVolume.CullBounds.
        internal const float HugeBoundsSize = 1000f;

        // XY-plane grid in [-1,1], z = 0 (matches the original lightgl plane mesh).
        internal static Mesh BuildGrid(int detail)
        {
            if (detail < 1) throw new System.ArgumentException($"Grid detail must be >= 1, got {detail}.", nameof(detail));

            int n = detail + 1;
            var verts = new Vector3[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    verts[i * n + j] = new Vector3(i / (float)detail * 2f - 1f, j / (float)detail * 2f - 1f, 0f);

            var tris = new int[detail * detail * 6];
            int t = 0;
            for (int i = 0; i < detail; i++)
                for (int j = 0; j < detail; j++)
                {
                    int a = i * n + j;
                    int b = (i + 1) * n + j;
                    int c = i * n + (j + 1);
                    int d = (i + 1) * n + (j + 1);
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }

            var mesh = new Mesh { name = "WaterGrid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeBoundsSize);
            return mesh;
        }
    }
}
