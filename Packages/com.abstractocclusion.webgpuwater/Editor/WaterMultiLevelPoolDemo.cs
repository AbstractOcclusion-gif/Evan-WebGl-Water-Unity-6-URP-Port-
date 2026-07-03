// TEMP validation builder — multi-level, multi-object pool demo.
// Purpose: exercise the water-region work end to end in one scene:
//   Phase 1  footprint gate  -> a prop below a pool's Y but OUTSIDE its footprint stays dry.
//   Phase 2  unified surface -> fog/darkening measure against the sampled surface, per body.
//   Phase 3  wet-face rule   -> a solid open-top "tank" (receiver mesh) shades inner faces only.
//   Phase 4  autolink        -> props carry NO WaterMembership; they self-link on Play.
// It reuses WaterBuildKit so bodies are wired exactly like the wizard's. DELETE THIS FILE
// (and the generated Assets/WebGLWater/TempMultiLevelDemo folder) once validation is done.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterMultiLevelPoolDemo
    {
        const string MenuPath = "Window/AbstractOcclusion/WebGpuWater/TEMP Multi-Level Pool Demo";
        const string DemoFolder = WaterBuildKit.Root + "/TempMultiLevelDemo";
        const string RootName = "TEMP MultiLevel Pool Demo";

        static readonly Vector3 PoolExtent = new Vector3(2.5f, 1f, 2.5f);
        static readonly Vector3 TankExtent = new Vector3(2f, 1.2f, 2f);

        const float PropScale = 0.6f;      // world size of a submerged prop cube
        const float PropSubmergeDepth = 0.35f; // how far below the surface a prop sits
        const string ShadeInnerFacesOnlyProp = "_ShadeInnerFacesOnly";
        const float ShadeInnerFacesOnlyOn = 1f;

        // Prop offsets from a pool centre, kept well inside the ±extent footprint.
        static readonly Vector2[] PropOffsets =
        {
            new Vector2(-1.2f, -1.2f), new Vector2(1.2f, -1.2f),
            new Vector2(-1.2f,  1.2f), new Vector2(1.2f,  1.2f),
        };

        // Pools at DIFFERENT world Y (the multi-level case) and offset in XZ so footprints
        // never overlap. Only the first is primary.
        struct Level { public string Name; public Vector3 Pos; public bool Primary; public bool GodRays; }
        static readonly Level[] Levels =
        {
            new Level { Name = "Pool L0 (primary)", Pos = new Vector3(0f,  0f,  0f), Primary = true,  GodRays = true  },
            new Level { Name = "Pool L1 (+2.5 up)", Pos = new Vector3(8f,  2.5f, 0f), Primary = false, GodRays = false },
            new Level { Name = "Pool L2 (-2 down)", Pos = new Vector3(0f, -2f,   8f), Primary = false, GodRays = false },
        };
        static readonly Vector3 TankPos = new Vector3(8f, 0f, 8f);

        [MenuItem(MenuPath)]
        static void Build()
        {
            var rootGO = new GameObject(RootName);

            if (!WaterBuildKit.CreateContext(rootGO.transform, out BuildContext ctx, DemoFolder, buildPoolMaterial: true))
            {
                Object.DestroyImmediate(rootGO);
                Debug.LogError("Multi-level demo: could not load water shaders; aborted.");
                return;
            }

            Material propMaterial = WaterBuildKit.LoadOrCreateMaterial(DemoFolder + "/DemoProp.mat", ctx.Shaders.Receiver);

            foreach (Level level in Levels)
            {
                WaterVolume body = WaterBuildKit.CreateWaterBody(
                    ctx, rootGO.transform, level.Name, level.Pos, PoolExtent,
                    primary: level.Primary, withPool: true, withGodRays: level.GodRays);

                // Submerged props, deliberately WITHOUT a WaterMembership: Phase 4 autolink
                // must give each one its own pool's uniforms on Play (a crate in L1 shows L1's
                // water, not the primary's).
                foreach (Vector2 offset in PropOffsets)
                {
                    Vector3 pos = level.Pos + new Vector3(offset.x, -PropSubmergeDepth, offset.y);
                    AddProp(body.transform, propMaterial, pos, Vector3.one * PropScale);
                }
            }

            AddDryControlProp(rootGO.transform, propMaterial);
            AddSolidTank(ctx, rootGO.transform);

            Selection.activeGameObject = rootGO;
            EditorSceneManager.MarkSceneDirty(rootGO.scene);
            Debug.Log("Built TEMP multi-level pool demo. Press Play: props autolink to their own " +
                      "pool; the control prop beside L0 stays dry; the tank shades inner faces only.");
        }

        // A prop placed BELOW pool L0's water Y but OUTSIDE its footprint. Phase 1 correct => it
        // renders as a plain dry surface (no blue tint / fog), proving the footprint gate.
        static void AddDryControlProp(Transform parent, Material material)
        {
            Vector3 pos = Levels[0].Pos + new Vector3(PoolExtent.x + 2f, -PropSubmergeDepth, 0f);
            GameObject prop = AddProp(parent, material, pos, Vector3.one * PropScale);
            prop.name = "Dry control prop (outside footprint)";
        }

        // A solid, open-top box acting as a pool via the RECEIVER shader with Shade Inner Faces
        // Only on: outer walls / underside stay dry, inner walls + floor read as water. Reuses
        // the pool mesh (an open-top [-1,1] box) scaled by the body's extent so it aligns with
        // the volume frame exactly. No analytic PoolWall here - this is the "any mesh" path.
        static void AddSolidTank(BuildContext ctx, Transform parent)
        {
            WaterVolume tank = WaterBuildKit.CreateWaterBody(
                ctx, parent, "Solid Tank (any-mesh pool)", TankPos, TankExtent,
                primary: false, withPool: false, withGodRays: false);

            Material tankMaterial = WaterBuildKit.LoadOrCreateMaterial(
                DemoFolder + "/DemoTank.mat", ctx.Shaders.Receiver,
                m => m.SetFloat(ShadeInnerFacesOnlyProp, ShadeInnerFacesOnlyOn));

            GameObject wall = WaterBuildKit.CreateRenderer("Tank Walls (receiver)", ctx.PoolMesh, tankMaterial, tank.transform);
            wall.transform.localPosition = Vector3.zero;
            wall.transform.localScale = TankExtent; // pool-space [-1,1] mesh -> world box matching the frame
            wall.GetComponent<MeshRenderer>().receiveShadows = true;
        }

        static GameObject AddProp(Transform parent, Material material, Vector3 worldPos, Vector3 scale)
        {
            GameObject prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prop.name = "Prop";
            prop.transform.SetParent(parent);
            prop.transform.position = worldPos;
            prop.transform.localScale = scale;
            prop.GetComponent<Renderer>().sharedMaterial = material;
            // Static visual validator: no physics needed, so drop the auto-added collider.
            Object.DestroyImmediate(prop.GetComponent<Collider>());
            return prop;
        }
    }
}
