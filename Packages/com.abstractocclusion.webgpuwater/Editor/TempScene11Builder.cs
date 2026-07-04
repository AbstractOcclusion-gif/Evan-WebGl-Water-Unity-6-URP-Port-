// TEMPORARY one-off builder for demo scene "11. Splashes and Foam".
//
// Delete this file after the scene is generated and moved into Samples~/Demos - it exists only
// to author the scene once, exactly like scenes 1-10 were built. It reuses WaterBuildKit's
// generators so this scene shares the same meshes/sky/tiles/material conventions as the rest,
// and writes its per-scene materials into the same Demos/Materials/<Name> layout.
//
// Menu: Tools > WebGpuWater TEMP > Build Scene 11 (Splashes + Foam)
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class TempScene11Builder
    {
        const string MenuPath = "Tools/WebGpuWater TEMP/Build Scene 11 (Splashes + Foam)";

        // Authoring layout mirrors scenes 1-10: scene under Demos/Scenes, per-scene materials
        // under Demos/Materials/<letters-of-scene-name> (matches WaterSceneBuilder's mapping).
        const string SceneName = "11. Splashes and Foam";
        const string DemosRoot = Root + "/Demos";
        const string ScenesFolder = DemosRoot + "/Scenes";
        const string MaterialsFolder = DemosRoot + "/Materials/SplashesandFoam";
        const string ScenePath = ScenesFolder + "/" + SceneName + ".unity";

        const string RootObjectName = "WebGL Water";
        const string WaterBodyName = "Water Body";
        const string FloatersParentName = "Floaters";

        // 5x5 water surface, 2 deep, expressed as WaterVolume half-extents (X/Z horizontal, Y depth).
        static readonly Vector3 WaterHalfExtents = new Vector3(2.5f, 1.0f, 2.5f);

        // Surface + edge foam are the point of this scene; keep them on. Border width is
        // WaterVolume's own default (see WaterWizardWindow.EdgeFoamBorderWidth).
        const float EdgeFoamBorderWidth = 0.08f;

        // Floor collider under the water so sunk props rest instead of falling forever
        // (same relative sizing WaterWizardWindow uses).
        const float FloorThickness = 0.1f;
        const float FloorDropBelowFloorMargin = 0.05f;
        const float FloorHorizontalScale = 2f;

        // Floatable props: dropped from just above the surface so they splash on entry.
        const float FloaterDropHeight = 0.9f;
        const float FloaterSpacingX = 1.2f;
        const float FloaterScale = 0.5f;
        const string UrpLitShaderName = "Universal Render Pipeline/Lit";
        const string UrpBaseColorProperty = "_BaseColor";

        [MenuItem(MenuPath)]
        static void BuildScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject(RootObjectName);
            if (!CreateContext(root.transform, out BuildContext ctx, MaterialsFolder, buildPoolMaterial: true))
            {
                Object.DestroyImmediate(root);
                return;
            }

            var body = CreateWaterBody(ctx, root.transform, WaterBodyName, Vector3.zero, WaterHalfExtents,
                                       primary: true, withPool: true, withGodRays: true, withFoamParticles: true);
            EnableFoam(body);

            CreateFloorForExtent(root.transform, WaterHalfExtents);
            CreateFloaters(root.transform, ctx.Splash);

            if (!SaveScene(scene))
            {
                Debug.LogError($"[TEMP] Failed to save scene at {ScenePath}.");
                return;
            }

            Selection.activeObject = root;
            Debug.Log($"[TEMP] Built '{SceneName}' at {ScenePath}. Verify in the editor, then move " +
                      "Demos/ into Samples~ and delete TempScene11Builder.cs.");
        }

        static void EnableFoam(WaterVolume body)
        {
            body.Foam = true;
            body.foamBorderWidth = EdgeFoamBorderWidth;
            EditorUtility.SetDirty(body);
        }

        static void CreateFloorForExtent(Transform parent, Vector3 extent)
        {
            var center = new Vector3(0f, -(extent.y + FloorDropBelowFloorMargin), 0f);
            var size = new Vector3(extent.x * FloorHorizontalScale, FloorThickness, extent.z * FloorHorizontalScale);
            CreateFloorCollider(parent, center, size);
        }

        // Three buoyant primitives spread across the surface, each with its own lit material so the
        // scene reads like the other object-pool demos. They fall in on Play and kick up splashes.
        static void CreateFloaters(Transform parent, WaterSplashEmitter splash)
        {
            var floaters = new GameObject(FloatersParentName);
            floaters.transform.SetParent(parent);

            CreateFloater(floaters.transform, PrimitiveType.Cube, "Floater Cube",
                          -FloaterSpacingX, new Color(0.85f, 0.35f, 0.30f), splash);
            CreateFloater(floaters.transform, PrimitiveType.Sphere, "Floater Sphere",
                          0f, new Color(0.30f, 0.55f, 0.85f), splash);
            CreateFloater(floaters.transform, PrimitiveType.Capsule, "Floater Capsule",
                          FloaterSpacingX, new Color(0.45f, 0.75f, 0.40f), splash);
        }

        static void CreateFloater(Transform parent, PrimitiveType shape, string name, float offsetX,
                                  Color color, WaterSplashEmitter splash)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(parent);
            go.transform.position = new Vector3(offsetX, FloaterDropHeight, 0f);
            go.transform.localScale = Vector3.one * FloaterScale;

            AssignFloaterMaterial(go, name, color);
            MakeFloatable(go, splash);
        }

        static void AssignFloaterMaterial(GameObject go, string name, Color color)
        {
            var shader = Shader.Find(UrpLitShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[TEMP] Shader '{UrpLitShaderName}' not found; leaving '{name}' on its default material.");
                return;
            }

            var material = LoadOrCreateMaterial(MaterialsFolder + "/" + name + ".mat", shader,
                                                m => m.SetColor(UrpBaseColorProperty, color));
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        // Same buoyant component set WaterWizardWindow.MakeFloatable applies. The splash emitter and
        // owning water body resolve themselves at runtime (WaterSplash auto-finds the emitter;
        // WaterMembership resolves its body by position), but the emitter is wired explicitly here
        // so a freshly built scene needs no play-mode round trip to look right.
        static void MakeFloatable(GameObject go, WaterSplashEmitter splash)
        {
            EnsureComponent<Rigidbody>(go);
            EnsureComponent<WaterInteractable>(go);
            EnsureComponent<WaterBuoyancy>(go);
            var splashComponent = EnsureComponent<WaterSplash>(go);
            if (splash != null) splashComponent.emitter = splash;
            EnsureComponent<WaterMembership>(go);
            EditorUtility.SetDirty(go);
        }

        static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        static bool SaveScene(UnityEngine.SceneManagement.Scene scene)
        {
            EnsureFolder(ScenesFolder);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (saved) AssetDatabase.SaveAssets();
            return saved;
        }
    }
}
