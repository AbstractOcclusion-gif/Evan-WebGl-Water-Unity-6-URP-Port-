// WebGL Water - demo scene builder (Unity 6 / URP port)
// Menu: Tools > WebGL Water > Demos > ...
//
// One command per demo, each showcasing a coherent slice of the feature set. Every demo
// composes WaterBuildKit primitives (shared assets, wired water bodies, props), builds into
// the CURRENT scene, and expects a fresh/empty scene (it creates its own camera, sun, splash).
// Each demo writes its materials into its OWN folder (Generated/Demos/<Name>) so building or
// rebuilding one scene never resets another scene's tuned materials.
using UnityEditor;
using UnityEngine;
using WebGLWater;
using static WebGLWater.EditorTools.WaterBuildKit;

namespace WebGLWater.EditorTools
{
    public static class WaterDemoBuilder
    {
        static readonly Color CrateColor = new Color(0.82f, 0.52f, 0.30f);
        static readonly Color PropColorA = new Color(0.30f, 0.62f, 0.78f);
        static readonly Color PropColorB = new Color(0.78f, 0.34f, 0.42f);
        static readonly Color PropColorC = new Color(0.52f, 0.74f, 0.36f);
        static readonly Color DeepBlue = new Color(0.02f, 0.10f, 0.15f);

        // ---- 1. Classic Pool -------------------------------------------------
        [MenuItem("Tools/WebGL Water/Demos/1. Classic Pool")]
        static void ClassicPool()
        {
            var root = new GameObject("Demo - Classic Pool");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("ClassicPool"))) { Object.DestroyImmediate(root); return; }

            var body = CreateWaterBody(ctx, root.transform, "Pool", Vector3.zero, Vector3.one,
                                       primary: true, withPool: true, withGodRays: true);
            body.windWaves = false;

            CreateFloorCollider(root.transform, new Vector3(0f, -1.05f, 0f), new Vector3(2f, 0.1f, 2f));
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube,
                               new Vector3(0.15f, 0.7f, -0.1f), 0.3f, CrateColor, "Crate");

            Finish(root, "Classic Pool");
        }

        // ---- 2. Deep Lake (depth showcase) -----------------------------------
        [MenuItem("Tools/WebGL Water/Demos/2. Deep Lake")]
        static void DeepLake()
        {
            var root = new GameObject("Demo - Deep Lake");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("DeepLake"))) { Object.DestroyImmediate(root); return; }

            const float depth = 8f;
            var body = CreateWaterBody(ctx, root.transform, "Deep Lake", Vector3.zero,
                                       new Vector3(2f, depth, 2f), primary: true, withPool: true, withGodRays: true);
            body.waterFog = true;
            body.depthDarken = true;
            body.depthDarkenStrength = 1f;
            body.causticDepthFade = 0.4f;
            body.godRayDepthFade = 0.3f;
            body.windWaves = true;
            body.windSpeed = 4f;

            // A static pillar spanning floor -> surface: reads the darkening + caustic fade along its height.
            CreateStaticObstacle(ctx, root.transform, new Vector3(-0.4f, -depth * 0.5f, 0.3f),
                                 new Vector3(0.5f, depth, 0.5f), "DeepPillar");

            CreateFloorCollider(root.transform, new Vector3(0f, -depth - 0.1f, 0f), new Vector3(4f, 0.1f, 4f));
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube,
                               new Vector3(0.2f, 1f, 0f), 0.3f, CrateColor, "DeepCrateA");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere,
                               new Vector3(-0.3f, 1.2f, -0.4f), 0.25f, PropColorA, "DeepCrateB");

            FrameCamera(ctx, new Vector3(0f, -depth * 0.4f, 0f), pitch: -18f, distance: depth * 1.4f);
            Finish(root, "Deep Lake");
        }

        // ---- 3. Terrain Lake (real bed depth + shoreline gradient) -----------
        [MenuItem("Tools/WebGL Water/Demos/3. Terrain Lake")]
        static void TerrainLake()
        {
            var root = new GameObject("Demo - Terrain Lake");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("TerrainLake"))) { Object.DestroyImmediate(root); return; }

            const float horizontal = 2f;
            const float depth = 4f;
            var body = CreateWaterBody(ctx, root.transform, "Terrain Lake", Vector3.zero,
                                       new Vector3(horizontal, depth, horizontal),
                                       primary: true, withPool: false, withGodRays: true);

            var terrain = CreateProceduralTerrain(ctx, root.transform, Vector3.zero, horizontal, depth);
            body.bedTerrain = terrain;
            body.useBedDepth = true;
            body.deepWaterColor = DeepBlue;
            body.shorelineFadeDepth = 3f;
            body.shorelineStrength = 0.85f;
            body.waterFog = true;
            body.depthDarken = true;
            body.windWaves = true;
            body.windSpeed = 3f;

            CreateFloatingProp(ctx, root.transform, PrimitiveType.Capsule,
                               new Vector3(1.2f, 0.5f, 0.3f), 0.2f, PropColorC, "TerrProp");

            FrameCamera(ctx, new Vector3(0f, -0.5f, 0f), pitch: -14f, distance: horizontal * 3.5f);
            Finish(root, "Terrain Lake (assign textures + tune shoreline on the WaterVolume)");
        }

        // ---- 4. Multi-Lake (multi-instance, varied size/depth) ---------------
        [MenuItem("Tools/WebGL Water/Demos/4. Multi-Lake")]
        static void MultiLake()
        {
            var root = new GameObject("Demo - Multi-Lake");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("MultiLake"))) { Object.DestroyImmediate(root); return; }

            var a = CreateWaterBody(ctx, root.transform, "Lake A (shallow)", new Vector3(0f, 0f, 0f),
                                    new Vector3(1.5f, 1.5f, 1.5f), primary: true, withPool: true, withGodRays: true);
            a.waterFog = true; a.depthDarken = true; a.windSpeed = 3f;

            var b = CreateWaterBody(ctx, root.transform, "Lake B (deep, choppy)", new Vector3(4.5f, 0f, 0f),
                                    new Vector3(2f, 5f, 1f), primary: false, withPool: true, withGodRays: true);
            b.waterFog = true; b.depthDarken = true; b.godRayDepthFade = 0.4f; b.windSpeed = 7f;

            var c = CreateWaterBody(ctx, root.transform, "Lake C (calm, wide)", new Vector3(-3.5f, 0f, 0.5f),
                                    new Vector3(2f, 1f, 1.2f), primary: false, withPool: true, withGodRays: true);
            c.windWaves = false;

            // Floating props on each lake (WaterMembership picks the containing body per frame).
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), 0.3f, CrateColor, "ML_A");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere, new Vector3(4.5f, 1f, 0f), 0.3f, PropColorB, "ML_B");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Capsule, new Vector3(-3.5f, 1f, 0.5f), 0.25f, PropColorC, "ML_C");

            FrameCamera(ctx, new Vector3(0.3f, -0.8f, 0.3f), pitch: -22f, distance: 12f);
            Finish(root, "Multi-Lake");
        }

        // ---- 5. Underwater ---------------------------------------------------
        [MenuItem("Tools/WebGL Water/Demos/5. Underwater")]
        static void Underwater()
        {
            var root = new GameObject("Demo - Underwater");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("Underwater"))) { Object.DestroyImmediate(root); return; }

            const float depth = 3f;
            var body = CreateWaterBody(ctx, root.transform, "Underwater", Vector3.zero,
                                       new Vector3(2f, depth, 2f), primary: true, withPool: true, withGodRays: true);
            body.waterFog = true;
            body.depthDarken = true;
            body.godRayDepthFade = 0.3f;
            body.windWaves = true;
            body.windSpeed = 3f;

            CreateStaticObstacle(ctx, root.transform, new Vector3(0.3f, -depth * 0.5f, -0.3f),
                                 new Vector3(0.4f, depth, 0.4f), "UW_Pillar");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube,
                               new Vector3(-0.2f, 0.6f, 0.2f), 0.3f, CrateColor, "UW_Crate");

            // Start the camera BELOW the surface, looking slightly up toward it.
            ctx.Orbit.pivot = new Vector3(0f, -1.4f, 0f);
            ctx.Orbit.pitch = 8f;
            ctx.Orbit.yaw = -200.5f;
            ctx.Orbit.distance = 1.0f;

            Finish(root, "Underwater (orbit up to break the surface)");
        }

        // ---- 6. Open Water (wind + foam + SSR) -------------------------------
        [MenuItem("Tools/WebGL Water/Demos/6. Open Water")]
        static void OpenWater()
        {
            var root = new GameObject("Demo - Open Water");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("OpenWater"))) { Object.DestroyImmediate(root); return; }

            var body = CreateWaterBody(ctx, root.transform, "Open Water", Vector3.zero,
                                       new Vector3(6f, 2f, 6f), primary: true, withPool: false, withGodRays: false);
            body.reflectionMode = WaterVolume.ReflectionMode.SSR;
            body.windWaves = true;
            body.windSpeed = 9f;
            body.waveAmplitudeScale = 5f;
            body.foam = true;
            body.foamStrength = 1f;
            body.waterFog = true;

            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube,
                               new Vector3(0.5f, 0.8f, 0.5f), 0.5f, CrateColor, "OW_Buoy");

            FrameCamera(ctx, new Vector3(0f, -0.3f, 0f), pitch: -12f, distance: 14f);
            Finish(root, "Open Water");
        }

        // ---- 7. Reflections Trio (SkyOnly / SSR / Planar) --------------------
        [MenuItem("Tools/WebGL Water/Demos/7. Reflections Trio")]
        static void ReflectionsTrio()
        {
            var root = new GameObject("Demo - Reflections Trio");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("ReflectionsTrio"))) { Object.DestroyImmediate(root); return; }

            var sky = CreateWaterBody(ctx, root.transform, "SkyOnly", new Vector3(-3f, 0f, 0f), Vector3.one,
                                      primary: true, withPool: false, withGodRays: false);
            sky.reflectionMode = WaterVolume.ReflectionMode.SkyOnly;
            sky.windSpeed = 4f;

            var ssr = CreateWaterBody(ctx, root.transform, "SSR", new Vector3(0f, 0f, 0f), Vector3.one,
                                      primary: false, withPool: false, withGodRays: false);
            ssr.reflectionMode = WaterVolume.ReflectionMode.SSR;
            ssr.windSpeed = 4f;

            var planar = CreateWaterBody(ctx, root.transform, "Planar", new Vector3(3f, 0f, 0f), Vector3.one,
                                         primary: false, withPool: false, withGodRays: false);
            planar.reflectionMode = WaterVolume.ReflectionMode.Planar;
            planar.windSpeed = 4f;

            // Props above each surface so there is something to reflect.
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere, new Vector3(-3f, 1f, 0f), 0.35f, PropColorA, "RT_Sky");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere, new Vector3(0f, 1f, 0f), 0.35f, PropColorB, "RT_SSR");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere, new Vector3(3f, 1f, 0f), 0.35f, PropColorC, "RT_Planar");

            FrameCamera(ctx, new Vector3(0f, -0.3f, 0f), pitch: -18f, distance: 10f);
            Finish(root, "Reflections Trio");
        }

        // ---- 8. Custom Object Pool ------------------------------------------
        [MenuItem("Tools/WebGL Water/Demos/8. Custom Object Pool")]
        static void CustomObjectPool()
        {
            var root = new GameObject("Demo - Custom Object Pool");
            if (!CreateContext(root.transform, out BuildContext ctx, DemoFolder("CustomObjectPool"))) { Object.DestroyImmediate(root); return; }

            var body = CreateWaterBody(ctx, root.transform, "Object Pool", Vector3.zero, Vector3.one,
                                       primary: true, withPool: true, withGodRays: true);
            body.windWaves = true;
            body.windSpeed = 2.5f;

            CreateFloorCollider(root.transform, new Vector3(0f, -1.05f, 0f), new Vector3(2f, 0.1f, 2f));
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cube, new Vector3(0.3f, 0.8f, 0.2f), 0.28f, CrateColor, "OP_Cube");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Sphere, new Vector3(-0.3f, 0.9f, -0.2f), 0.24f, PropColorA, "OP_Sphere");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Capsule, new Vector3(0.0f, 1.0f, 0.4f), 0.2f, PropColorB, "OP_Capsule");
            CreateFloatingProp(ctx, root.transform, PrimitiveType.Cylinder, new Vector3(-0.4f, 0.85f, 0.3f), 0.2f, PropColorC, "OP_Cylinder");
            CreateStaticObstacle(ctx, root.transform, new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.35f, 1f, 0.35f), "OP_Rock");

            Finish(root, "Custom Object Pool");
        }

        // ---- helpers ---------------------------------------------------------
        static string DemoFolder(string name) => Gen + "/Demos/" + name;

        static void FrameCamera(BuildContext ctx, Vector3 pivot, float pitch, float distance)
        {
            if (ctx.Orbit == null) return;
            ctx.Orbit.pivot = pivot;
            ctx.Orbit.pitch = pitch;
            ctx.Orbit.distance = distance;
        }

        static void Finish(GameObject root, string label)
        {
            Selection.activeObject = root;
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGL Water] Demo '{label}' built. Press Play.");
        }
    }
}
