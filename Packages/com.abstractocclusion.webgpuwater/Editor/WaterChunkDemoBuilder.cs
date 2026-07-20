// WebGpuWater - one-click demo scene for the volumetric water CHUNK (integrated form).
//
// A chunk is now ONE WaterVolume configured as a sphere footprint: it renders the real disc surface
// (foam / above-below / reflections) AND owns the submerged fog shell, both fed from its own per-body
// block - so the surface and the shell share one wave source (no seam) and it needs no external
// primary. The demo floats one such chunk above a calm sea (a chunk at sea level would blend into it).
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterChunkDemoBuilder
    {
        const string MenuPath = WaterBuildKit.MenuRoot + "Build Chunk Demo Scene";
        const string DemoAssetFolder = WaterBuildKit.Root + "/Demos/Materials/ChunkDemo";
        const string DemoRootName = "Chunk Demo";
        const string SeaBodyName = "Sea";
        const string ChunkName = "Water Chunk (Sphere)";
        const string UndoLabel = "Build Chunk Demo";

        static readonly Vector3 SeaPosition = Vector3.zero;
        static readonly Vector3 SeaExtent = new Vector3(24f, 1f, 24f);

        static readonly Vector3 ChunkCenter = new Vector3(0f, 6f, 0f);
        const float ChunkRadius = 4f;         // sphere radius; the body extent is (R, R, R)
        const float ChunkDensityBoost = 1.4f; // reads clearly against the sky behind it

        // Disc tessellation for the edit-mode (author-time) surface preview; play mode rebuilds a disc
        // at the sim resolution via WaterVolume.discSurface.
        const int SurfaceDiscRadialDetail = 48;
        const int SurfaceDiscAngularSegments = 96;

        [MenuItem(MenuPath)]
        internal static void BuildChunkDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Undo.SetCurrentGroupName(UndoLabel);
            int undoGroup = Undo.GetCurrentGroup();

            var root = new GameObject(DemoRootName);
            Undo.RegisterCreatedObjectUndo(root, UndoLabel);

            if (!WaterBuildKit.CreateContext(root.transform, out var ctx, DemoAssetFolder))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            // Foam particles OFF on both bodies: they need a baked shore-depth texture the flat demo
            // bodies don't have. The surface shader's own whitecap foam still renders.
            WaterBuildKit.CreateWaterBody(ctx, root.transform, SeaBodyName, SeaPosition, SeaExtent,
                                          primary: true, withPool: false, withGodRays: true,
                                          withFoamParticles: false);

            GameObject chunk = CreateSphereChunk(ctx, root.transform, ChunkName, ChunkCenter, ChunkRadius);

            if (ctx.Orbit != null) ctx.Orbit.pivot = ChunkCenter;

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = chunk;
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[WebGpuWater] Chunk demo built: ONE WaterVolume as a sphere chunk (real disc " +
                      "surface + owned fog shell, one wave source) floating above a calm sea. Enter Play " +
                      "or orbit in Game view. Tune chunk fields on the '" + ChunkName + "' body.");
        }

        // One WaterVolume configured as a sphere chunk: a real bounded surface body (foam / above-below
        // / reflections) that ALSO owns the fog shell. Non-primary so it doesn't fight the sea for the
        // globals; its own fog off (the shell renders the volume); surface swapped to a disc.
        // Internal: the feature-showcase builder reuses this exact rig for its chunk finale station.
        internal static GameObject CreateSphereChunk(BuildContext ctx, Transform parent, string name,
                                                     Vector3 center, float radius)
        {
            var extent = new Vector3(radius, radius, radius);
            WaterVolume body = WaterBuildKit.CreateWaterBody(ctx, parent, name, center, extent,
                primary: false, withPool: false, withGodRays: false, withFoamParticles: false);

            body.discSurface = true;   // round surface footprint (play-mode rebuild makes a disc)
            body.WaterFog = false;     // the shell owns the fog volume
            body.chunkFootprint = WaterVolume.ChunkFootprint.Sphere;
            body.chunkDensityBoost = ChunkDensityBoost;

            Mesh disc = WaterMeshBuilder.BuildDisc(SurfaceDiscRadialDetail, SurfaceDiscAngularSegments);
            ReplaceSurfaceMesh(body.surfaceAbove, disc);
            ReplaceSurfaceMesh(body.surfaceUnder, disc);
            EditorUtility.SetDirty(body);

            // CreateWaterBody nests the body under a "name" root; return that root for framing/selection.
            return body.transform.parent != null ? body.transform.parent.gameObject : body.gameObject;
        }

        static void ReplaceSurfaceMesh(Renderer surface, Mesh mesh)
        {
            if (surface == null) return;
            var filter = surface.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = mesh;
        }
    }
}
