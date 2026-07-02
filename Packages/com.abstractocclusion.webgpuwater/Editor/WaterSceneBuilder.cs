// WebGL Water - editor utilities backing the Water Wizard window.
//
// Menu entries were removed in favour of a single wizard (see WaterWizardWindow); these
// methods are the retrofit/one-off operations the wizard exposes as buttons. Scene CREATION
// lives in the wizard itself - this file only holds operations that act on an EXISTING scene
// selection or prefab. Thin layer over WaterBuildKit (which owns the reusable generators).
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterSceneBuilder
    {
        const string WaterVolumePrefabPath = WaterBuildKit.Root + "/WaterVolume.prefab";
        const string WaterVolumeObjectName = "WaterVolume";
        const string DemoMaterialsRoot = WaterBuildKit.Root + "/Demos/Materials/";

        // A tidy, reusable single-body prefab: one WaterVolume + its two water renderers, with the
        // asset refs baked in. Scene refs (camera, sun) resolve at runtime, so it works when dropped
        // into a scene.
        internal static void CreateWaterVolumePrefab()
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

        // Retrofit GPU foam particles onto an existing body. Demo scenes are create-once,
        // so they don't pick the feature up automatically; this wires the compute, the
        // procedural-quad material (shared, in Generated/) and the component in one click.
        internal static void AddFoamParticlesToSelection()
        {
            var selected = Selection.activeGameObject;
            var volume = selected != null ? selected.GetComponentInChildren<WaterVolume>() : null;
            if (volume == null)
            {
                Debug.LogError("[WebGL Water] Select a GameObject with a WaterVolume first.");
                return;
            }
            if (volume.GetComponent<WaterFoamParticles>() != null)
            {
                Debug.LogWarning("[WebGL Water] That body already has foam particles.");
                return;
            }
            EnsureGenFolder();
            AddFoamParticles(volume, MaterialFolderForActiveScene());
            Selection.activeObject = volume.gameObject;
        }

        // Upgrade the shared splash materials (Generated/SplashDroplet.mat + SplashCrown.mat)
        // to the lit splash shader in place. They are shared by every demo scene, so one
        // click upgrades them all; hand-tuned values on matching properties are kept.
        internal static void UpgradeSplashMaterialsMenu()
        {
            UpgradeSplashMaterials();
            Debug.Log("[WebGL Water] Splash materials now use " + ShaderSplashParticles + ".");
        }

        // Assign the animated foam flipbook + relief normal map to every water surface
        // material in the open scene (above AND under). Demo materials are create-once,
        // so new foam textures don't reach them automatically; this is the retrofit.
        internal static void AssignFoamTexturesToSceneWater()
        {
            var volumes = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            if (volumes.Length == 0)
            {
                Debug.LogError("[WebGL Water] No WaterVolume in the open scene.");
                return;
            }

            int touched = 0;
            foreach (WaterVolume volume in volumes)
            {
                touched += AssignFoamTextures(volume.surfaceAbove);
                touched += AssignFoamTextures(volume.surfaceUnder);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGL Water] Foam flipbook + normal map assigned to {touched} water material(s).");
        }

        static int AssignFoamTextures(Renderer surface)
        {
            if (surface == null || surface.sharedMaterial == null) return 0;
            AssignFoamFlipbook(surface.sharedMaterial);
            EditorUtility.SetDirty(surface.sharedMaterial);
            return 1;
        }

        // The per-demo material folder for the open scene ("3. Terrain Lake" ->
        // Demos/Materials/TerrainLake), so a retrofitted material lives (and is tweaked)
        // next to that demo's other materials. Falls back to Generated/ for custom scenes.
        static string MaterialFolderForActiveScene()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var compact = new System.Text.StringBuilder(sceneName.Length);
            foreach (char c in sceneName)
                if (char.IsLetter(c)) compact.Append(c);
            string candidate = DemoMaterialsRoot + compact;
            return AssetDatabase.IsValidFolder(candidate) ? candidate : Gen;
        }

        // Adds a SECOND (non-primary) water body next to the primary, sharing the sun, camera,
        // compute and shaders. The new body renders through its own MaterialPropertyBlock, so it
        // must look independent - proof the de-globalisation works.
        internal static void AddSecondaryBody()
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
