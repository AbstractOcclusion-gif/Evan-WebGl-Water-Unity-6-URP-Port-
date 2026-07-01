// WebGL Water - one-click scene builder (Unity 6 / URP port)
// Menu: Tools > WebGL Water > Build Scene
//
// Thin scene-composition layer over WaterBuildKit (which owns the reusable generators:
// meshes, sky, tiles, materials, camera/sun/splash, and the wired water body). The
// analytic pool (walls/floor rendered by PoolWall.shader, showing caustics) is OPTIONAL -
// leave it off if you've built your own pool.
using UnityEditor;
using UnityEngine;
using WebGLWater;
using static WebGLWater.EditorTools.WaterBuildKit;

namespace WebGLWater.EditorTools
{
    public static class WaterSceneBuilder
    {
        const string WaterVolumePrefabPath = WaterBuildKit.Root + "/WaterVolume.prefab";
        const string WaterVolumeObjectName = "WaterVolume";

        [MenuItem("Tools/WebGL Water/Build Scene (with analytic pool)")]
        static void BuildWithPool() => Build(true);

        [MenuItem("Tools/WebGL Water/Build Scene (water only - keep my pool)")]
        static void BuildWaterOnly() => Build(false);

        static void Build(bool buildAnalyticPool)
        {
            var root = new GameObject("WebGL Water");
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildAnalyticPool))
            {
                Object.DestroyImmediate(root);
                return;
            }

            CreateWaterBody(ctx, root.transform, "Water Body", Vector3.zero, Vector3.one,
                            primary: true, withPool: buildAnalyticPool, withGodRays: true);

            // Demo interaction: a floor to catch objects + a falling crate carrying the two-way
            // coupling components.
            CreateFloorCollider(root.transform, new Vector3(0f, -1.05f, 0f), new Vector3(2f, 0.1f, 2f));
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube,
                               new Vector3(0.15f, 0.7f, -0.1f), 0.3f, new Color(0.82f, 0.52f, 0.30f), "Crate");

            Selection.activeObject = root;
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();

            Debug.Log("[WebGL Water] Scene built. Press Play.  " +
                      (buildAnalyticPool ? "Analytic pool included." : "No pool created - using your own.") +
                      "  Assign your pool tile texture to the Water Controller's 'Tiles' field for matching reflections.");
        }

        // A tidy, reusable single-body prefab: one WaterVolume + its two water renderers, with the
        // asset refs baked in. Scene refs (camera, sun) resolve at runtime, so it works when dropped
        // into a scene.
        [MenuItem("Tools/WebGL Water/Create WaterVolume Prefab (water only)")]
        static void CreateWaterVolumePrefab()
        {
            EnsureGenFolder();
            if (!TryLoadShaders(out ShaderSet shaders)) return;

            var grid = SaveAsset(BuildGrid(GridDetail), Gen + "/WaterGrid.asset");
            var sky = SaveCubemap(BuildSky(SkyCubemapSize), Gen + "/SkyCubemap.cubemap");
            var tiles = LoadOrBuildTiles(Gen + "/Tiles.png");
            var (matAbove, matUnder, _) = CreateWaterMaterials(shaders.Water, shaders.Pool, buildAnalyticPool: false, Gen);

            var root = new GameObject(WaterVolumeObjectName);
            var volume = root.AddComponent<WaterVolume>();
            var above = CreateRenderer("Water (above)", grid, matAbove, root.transform);
            var under = CreateRenderer("Water (under)", grid, matUnder, root.transform);

            volume.simCompute = shaders.Compute;
            volume.causticsShader = shaders.Caustics;
            volume.obstacleShader = shaders.Obstacle;
            volume.waterMesh = grid;
            volume.tiles = tiles;
            volume.sky = sky;
            volume.quality = LoadOrCreateWaterQuality(Gen + "/WaterQuality.asset");
            volume.surfaceAbove = above.GetComponent<Renderer>();
            volume.surfaceUnder = under.GetComponent<Renderer>();
            volume.isPrimary = true;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, WaterVolumePrefabPath);
            Object.DestroyImmediate(root); // remove the temp build object; only the prefab persists
            AssetDatabase.SaveAssets();

            if (prefab == null)
            {
                Debug.LogError($"[WebGL Water] Failed to save the WaterVolume prefab at {WaterVolumePrefabPath}.");
                return;
            }

            Selection.activeObject = prefab;
            Debug.Log($"[WebGL Water] WaterVolume prefab created at {WaterVolumePrefabPath}. " +
                      "Drop it into a scene with a camera - it resolves the camera and sun automatically.");
        }

        // Adds a SECOND (non-primary) water body next to the primary, sharing the sun, camera,
        // compute and shaders. The new body renders through its own MaterialPropertyBlock, so it
        // must look independent - proof the de-globalisation works.
        [MenuItem("Tools/WebGL Water/Add Water Body (secondary)")]
        static void AddSecondaryBody()
        {
            var all = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            if (all == null || all.Length == 0)
            {
                Debug.LogError("[WebGL Water] Build the scene first (no WaterVolume found).");
                return;
            }
            WaterVolume primary = System.Array.Find(all, c => c.isPrimary) ?? all[0];

            var bodyRoot = new GameObject("Water Body (secondary)");

            var frameGO = new GameObject("Frame (WaterVolume)");
            frameGO.transform.SetParent(bodyRoot.transform);
            float offsetX = 2f * primary.volumeExtent.x + 1f;
            frameGO.transform.position = primary.transform.position + new Vector3(offsetX, 0f, 0f);

            var ctrl = frameGO.AddComponent<WaterVolume>();
            ctrl.simCompute = primary.simCompute;
            ctrl.causticsShader = primary.causticsShader;
            ctrl.obstacleShader = primary.obstacleShader;
            ctrl.waterMesh = primary.waterMesh;
            ctrl.targetCamera = primary.targetCamera;
            ctrl.sun = primary.sun;
            ctrl.tiles = primary.tiles;
            ctrl.sky = primary.sky;
            ctrl.quality = primary.quality;
            ctrl.volumeExtent = primary.volumeExtent;
            ctrl.isPrimary = false; // only ONE body mirrors to globals

            var rendGO = new GameObject("Renderers");
            rendGO.transform.SetParent(bodyRoot.transform);
            ctrl.surfaceAbove = CloneBodyRenderer(primary.surfaceAbove, rendGO.transform, "Water (above)");
            ctrl.surfaceUnder = CloneBodyRenderer(primary.surfaceUnder, rendGO.transform, "Water (under)");
            ctrl.poolRenderer = CloneBodyRenderer(primary.poolRenderer, rendGO.transform, "Analytic Pool");
            ctrl.godRayRenderer = CloneBodyRenderer(primary.godRayRenderer, rendGO.transform, "God Rays");

            Selection.activeObject = bodyRoot;
            EditorUtility.SetDirty(ctrl);
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[WebGL Water] Secondary water body added. Move its 'Frame' child to reposition; " +
                      "edit that Water Controller's Volume Extent for a different size/shape.");
        }

        // Copy a body renderer (same mesh + material + world transform, so its object->world maps to
        // the same pool space as the source); per-body data arrives via the MPB.
        static Renderer CloneBodyRenderer(Renderer src, Transform parent, string name)
        {
            if (src == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
            go.transform.localScale = src.transform.lossyScale;

            var srcFilter = src.GetComponent<MeshFilter>();
            if (srcFilter != null) go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = src.sharedMaterial;
            mr.shadowCastingMode = src.shadowCastingMode;
            mr.receiveShadows = src.receiveShadows;
            return mr;
        }
    }
}
