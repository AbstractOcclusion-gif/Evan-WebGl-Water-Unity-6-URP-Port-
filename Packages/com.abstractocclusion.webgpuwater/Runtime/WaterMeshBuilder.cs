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

        // Unit cube spanning [-0.5, 0.5] per axis: the exclusion volume's water-wall mesh, drawn
        // with the volume's box-to-world matrix. Positions only (8 verts / 12 tris) - the wall
        // shader shades from world position and renders Cull Off, so normals/uvs/winding senses
        // are irrelevant. Unit bounds: DrawMesh culls with the real box size via the matrix.
        internal static Mesh BuildUnitCube()
        {
            const float Half = 0.5f;
            var verts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                verts[i] = new Vector3((i & 1) != 0 ? Half : -Half,
                                       (i & 2) != 0 ? Half : -Half,
                                       (i & 4) != 0 ? Half : -Half);
            // Two triangles per face; vertex index bits are (x, y, z).
            int[] tris =
            {
                0, 2, 1,  2, 3, 1, // -z
                4, 5, 6,  5, 7, 6, // +z
                0, 4, 2,  4, 6, 2, // -x
                1, 3, 5,  3, 7, 5, // +x
                0, 1, 4,  1, 5, 4, // -y
                2, 6, 3,  6, 7, 3, // +y
            };
            var mesh = new Mesh { name = "WaterExclusionWallCube" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return mesh;
        }
    }
}
