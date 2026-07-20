// WebGpuWater - one-click FEATURE SHOWCASE scene (Crest-Examples-style).
//
// One scene, ~16 stations, one feature each: a WaterShowcaseController cycles INACTIVE station
// templates (N / M keys, or the controller's Previous / Next inspector buttons), instantiating
// exactly one live clone at a time so every station starts from fresh sim state. Mirrors the
// structure of Crest's Examples.unity sample (controller + self-contained station prefabs),
// composed entirely from WaterBuildKit's generators + the existing per-body knobs.
//
// Everything a station needs travels inside its template (bodies, terrain, props, helpers);
// the scene-level rig (camera, sun, splash) is shared and comes from CreateContext. Private
// serialized blocks are configured through WaterVolumePropertyPaths (the shared registry),
// exactly like the Water Wizard does.
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterShowcaseBuilder
    {
        const string MenuPath = MenuRoot + "Build Feature Showcase Scene";
        const string DemoAssetFolder = Root + "/Demos/Materials/FeatureShowcase";
        const string RootName = "Feature Showcase";
        const string StationsRootName = "Stations";
        const string ControllerName = "Showcase Controller";
        const string UndoLabel = "Build Feature Showcase";

        // Create-once station assets (delete to regenerate, like every generated asset).
        const string BeachTerrainAssetPath = DemoAssetFolder + "/BeachTerrain.asset";
        const string ShelfTerrainAssetPath = DemoAssetFolder + "/ShelfTerrain.asset";
        const string WarmPropMaterialPath = DemoAssetFolder + "/PropWarm.mat";
        const string CoolPropMaterialPath = DemoAssetFolder + "/PropCool.mat";
        const string LightPropMaterialPath = DemoAssetFolder + "/PropLight.mat";

        static readonly Color WarmPropColor = new Color(0.85f, 0.45f, 0.30f);
        static readonly Color CoolPropColor = new Color(0.30f, 0.55f, 0.85f);
        static readonly Color LightPropColor = new Color(0.92f, 0.92f, 0.88f);

        // Shared floater tuning (mirrors the Water Wizard's presets: Light rides high, Heavy sits low).
        const float FloaterLinearDamping = 2.0f;
        const float FloaterAngularDamping = 1.0f;
        const float LightBuoyancy = 4.0f;
        const float NormalBuoyancy = 2.5f;
        const float HeavyBuoyancy = 1.2f;
        // Metres above the rest surface a floater spawns, so it drops in and settles visibly.
        const float FloaterSpawnHeight = 1f;

        // Floor collider sizing relative to the body extent (same convention as the wizard).
        const float FloorThickness = 0.1f;
        const float FloorDropMargin = 0.05f;
        const float FloorHorizontalScale = 2f;

        // Dripper visual: a small bulb hovering over the emit point so the drip source is findable.
        const float DripperVisualHeight = 0.35f;
        const float DripperVisualSize = 0.15f;

        // Terrain bake grid. 65 is plenty for a demo beach and keeps the create-once asset tiny.
        const int TerrainHeightmapResolution = 65;

        // Serialized path into WaterSphereInteractor (private field; same access pattern as
        // WaterVolumePropertyPaths, kept here because it is the only consumer).
        const string SphereInteractorStrengthPath = "strength";

        // ---------------------------------------------------------------- entry point
        [MenuItem(MenuPath)]
        internal static void BuildShowcaseScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Undo.SetCurrentGroupName(UndoLabel);
            int undoGroup = Undo.GetCurrentGroup();

            var root = NewUndoableGameObject(RootName);
            if (!CreateContext(root.transform, out BuildContext ctx, DemoAssetFolder))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            var stationsRoot = NewUndoableGameObject(StationsRootName);
            stationsRoot.transform.SetParent(root.transform);

            var controllerGO = NewUndoableGameObject(ControllerName);
            controllerGO.transform.SetParent(root.transform);
            var controller = Undo.AddComponent<WaterShowcaseController>(controllerGO);
            controller.orbitCamera = ctx.Orbit;
            controller.sun = ctx.Sun;

            StationSpec[] specs = StationSpecs();
            for (int i = 0; i < specs.Length; i++)
                controller.stations.Add(BuildStation(ctx, stationsRoot.transform, specs[i], i));

            EditorUtility.SetDirty(controller);
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = controllerGO;
            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Feature showcase built: {specs.Length} stations. Enter Play and " +
                      "cycle with N / M (or use the Showcase Controller's Previous / Next buttons).");
        }

        // ---------------------------------------------------------------- station table
        // Camera framing values are demo tuning, kept next to each station's name/description so a
        // station reads as one block.
        readonly struct StationSpec
        {
            public readonly string Name;
            public readonly string Description;
            public readonly Vector3 Pivot;
            public readonly float Pitch, Yaw, Distance, MinDistance, MaxDistance;
            public readonly Vector3? SunEuler; // null = keep the shared sun pose
            public readonly Action<BuildContext, Transform> Build;

            public StationSpec(string name, string description, Vector3 pivot, float pitch, float yaw,
                               float distance, float minDistance, float maxDistance,
                               Action<BuildContext, Transform> build, Vector3? sunEuler = null)
            {
                Name = name;
                Description = description;
                Pivot = pivot;
                Pitch = pitch;
                Yaw = yaw;
                Distance = distance;
                MinDistance = minDistance;
                MaxDistance = maxDistance;
                Build = build;
                SunEuler = sunEuler;
            }
        }

        // The default demo yaw (matches WaterBuildKit's orbit framing) - most stations reuse it.
        const float DemoYaw = -200.5f;
        // Stations viewed across their feature gradient (beach, clarity shelf, reflection gates)
        // look straight down +Z instead.
        const float FacingPlusZYaw = 180f;

        static StationSpec[] StationSpecs() => new[]
        {
            new StationSpec("Interactive Ripples",
                "Click / tap to splash, drag to draw. The dripper writes into the same GPU ripple sim.",
                Vector3.zero, -35f, DemoYaw, 5f, 1.5f, 12f, BuildRipplesStation),

            new StationSpec("Wind Waves",
                "Analytic wind-wave bank layered on the ripple sim - speed, scale and spread are per-body knobs.",
                Vector3.zero, -25f, DemoYaw, 10f, 2f, 30f, BuildWindWavesStation),

            new StationSpec("Ocean & Horizon",
                "FFT ocean with a camera-following horizon clipmap, swell, choppiness and horizon haze.",
                new Vector3(0f, 1f, 0f), -10f, DemoYaw, 25f, 3f, 80f, BuildOceanStation),

            new StationSpec("Buoyancy",
                "Probe buoyancy, three presets: light, normal, heavy. Props bob, drift with waves and splash.",
                Vector3.zero, -30f, DemoYaw, 7f, 1.5f, 16f, BuildBuoyancyStation),

            new StationSpec("Sphere Wake",
                "A moving sphere injects a Crest-style velocity dipole - a real V-wake, plus foam in its trail.",
                Vector3.zero, -45f, DemoYaw, 13f, 2f, 30f, BuildWakeStation),

            new StationSpec("Whitecaps & Foam",
                "Wind-driven whitecaps: turbulence-fed surface foam with an animated pattern flipbook.",
                Vector3.zero, -30f, DemoYaw, 10f, 2f, 25f, BuildFoamStation),

            new StationSpec("Shoreline & Surf",
                "Terrain-baked shore: shoaling, surf fronts, swash and foam particles from one depth + SDF bake.",
                new Vector3(0f, 0f, 2f), -18f, FacingPlusZYaw, 14f, 2f, 35f, BuildShorelineStation),

            new StationSpec("Caustics & Occluder Shadows",
                "Projected caustics on the pool - and the sphere's shadow is REFRACTED into the caustic field.",
                Vector3.zero, -40f, DemoYaw, 4f, 1.5f, 8f, BuildCausticsStation),

            new StationSpec("God Rays",
                "Caustic-masked light shafts. You are underwater - swing the view across the sun.",
                new Vector3(0f, -1.2f, 0f), 5f, DemoYaw, 3f, 1f, 5f, BuildGodRaysStation),

            new StationSpec("Underwater & Waterline",
                "Underwater fog with a live wavy waterline and meniscus - dip the camera through the surface.",
                new Vector3(0f, -0.15f, 0f), 2f, DemoYaw, 2.4f, 1f, 8f, BuildUnderwaterStation),

            new StationSpec("Scattering & SSS",
                "Lit volume scattering + wave-crest subsurface glow. Look toward the low sun.",
                Vector3.zero, -8f, -160f, 11f, 2f, 25f, BuildScatteringStation,
                sunEuler: new Vector3(8f, 200f, 0f)),

            new StationSpec("Reflections Trio",
                "Same water, three modes: sky only (left), screen-space (middle), planar mirror (right).",
                new Vector3(0f, 0.6f, 0f), -18f, FacingPlusZYaw, 12f, 2f, 25f, BuildReflectionsStation),

            new StationSpec("Exclusion Volume",
                "A Crest-style carve: a dry room below the waterline - walls, fog cut, wavy edge.",
                Vector3.zero, -30f, DemoYaw, 6f, 1.5f, 12f, BuildExclusionStation),

            new StationSpec("Depth Clarity",
                "Clarity derived from bed depth: one curve drives see-through shallows into opaque deep.",
                Vector3.zero, -50f, FacingPlusZYaw, 15f, 2f, 35f, BuildClarityStation),

            new StationSpec("Multi-Body & Time Scale",
                "Two independent bodies, per-body time - the right pond runs in slow motion.",
                Vector3.zero, -35f, DemoYaw, 12f, 2f, 25f, BuildMultiBodyStation),

            new StationSpec("Water Chunk (Finale)",
                "A self-contained chunk of ocean floating in dry space - and its fill level is animating. " +
                "Box and arbitrary-mesh chunks are supported too.",
                new Vector3(0f, 6f, 0f), -10f, DemoYaw, 12f, 3f, 30f, BuildChunkStation),
        };

        static WaterShowcaseStation BuildStation(BuildContext ctx, Transform parent,
                                                 in StationSpec spec, int index)
        {
            var template = NewUndoableGameObject($"Station {index + 1:00} - {spec.Name}");
            template.transform.SetParent(parent);

            var station = Undo.AddComponent<WaterShowcaseStation>(template);
            station.displayName = spec.Name;
            station.description = spec.Description;
            station.orbitPivot = spec.Pivot;
            station.orbitPitch = spec.Pitch;
            station.orbitYaw = spec.Yaw;
            station.orbitDistance = spec.Distance;
            station.orbitMinDistance = spec.MinDistance;
            station.orbitMaxDistance = spec.MaxDistance;
            station.overrideSun = spec.SunEuler.HasValue;
            station.sunEuler = spec.SunEuler ?? Vector3.zero;

            spec.Build(ctx, template.transform);

            template.SetActive(false); // templates sleep; the controller instantiates live clones
            EditorUtility.SetDirty(station);
            return station;
        }

        // ---------------------------------------------------------------- stations
        static void BuildRipplesStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(3f, 0.75f, 3f);
            const float DripInterval = 1.4f;
            var dripPosition = new Vector3(1.4f, 0f, 1.1f);

            WaterVolume body = CreateWaterBody(ctx, root, "Ripple Pond", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.WindWaves = false; // dead-calm surface so individual ripples read
            CreateDripper(root, dripPosition, DripInterval);
        }

        static void BuildWindWavesStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(6f, 1f, 6f);
            const float WindSpeed = 6f;
            const float WaveScaleMeters = 12f;
            const float WaveAmplitudeScale = 5f;

            WaterVolume body = CreateWaterBody(ctx, root, "Wind Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            ConfigureBody(body,
                (WaterVolumePropertyPaths.WindSpeed, WindSpeed),
                (WaterVolumePropertyPaths.WaveScaleMeters, WaveScaleMeters),
                (WaterVolumePropertyPaths.WaveAmplitudeScale, WaveAmplitudeScale));
        }

        static void BuildOceanStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(60f, 6f, 60f);
            const float LargeWaveAmplitude = 1.2f;
            const float LargeWaveChoppiness = 0.5f;
            const float SwellHeight = 0.7f;
            const float SwellWavelength = 45f;
            const float HorizonHazeDensity = 0.3f;

            WaterVolume body = CreateWaterBody(ctx, root, "Open Ocean", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.WaterFog = true;        // diving under the swell should read as ocean, not air
            body.configureCamera = true; // the horizon clipmap needs the ocean far plane
            WithSerialized(body, so =>
            {
                SetBool(so, WaterVolumePropertyPaths.OpenWater, true);
                SetBool(so, WaterVolumePropertyPaths.UnboundedOcean, true);
                SetEnum(so, WaterVolumePropertyPaths.BodyType, (int)WaterVolume.WaterBodyType.Ocean);
                SetFloat(so, WaterVolumePropertyPaths.LargeWaveAmplitude, LargeWaveAmplitude);
                SetFloat(so, WaterVolumePropertyPaths.LargeWaveChoppiness, LargeWaveChoppiness);
                SetFloat(so, WaterVolumePropertyPaths.SwellHeight, SwellHeight);
                SetFloat(so, WaterVolumePropertyPaths.SwellWavelength, SwellWavelength);
                SetFloat(so, WaterVolumePropertyPaths.HorizonHazeDensity, HorizonHazeDensity);
            });
        }

        static void BuildBuoyancyStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(4f, 1.5f, 4f);
            const float PropSize = 0.5f;

            CreateWaterBody(ctx, root, "Buoyancy Pond", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            CreateFloorCollider(root, new Vector3(0f, -(extent.y + FloorDropMargin), 0f),
                new Vector3(extent.x * FloorHorizontalScale, FloorThickness, extent.z * FloorHorizontalScale));

            CreateFloater(root, PrimitiveType.Cube, "Floater (light)",
                new Vector3(-1.5f, FloaterSpawnHeight, 0f), PropSize, LightBuoyancy, LightMaterial());
            CreateFloater(root, PrimitiveType.Sphere, "Floater (normal)",
                new Vector3(0f, FloaterSpawnHeight, 1f), PropSize, NormalBuoyancy, CoolMaterial());
            CreateFloater(root, PrimitiveType.Capsule, "Floater (heavy)",
                new Vector3(1.5f, FloaterSpawnHeight, -0.5f), PropSize, HeavyBuoyancy, WarmMaterial());
        }

        static void BuildWakeStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(10f, 1.5f, 10f);
            const float WindSpeed = 2f;      // gentle background so the wake dominates
            const float SphereSize = 0.8f;   // half-submerged at the path height below
            const float PathRadius = 4f;
            const float LapSeconds = 12f;
            const float WakeStrength = 1.5f;

            WaterVolume body = CreateWaterBody(ctx, root, "Wake Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.Foam = true; // the wake's turbulence leaves a foam trail
            ConfigureBody(body, (WaterVolumePropertyPaths.WindSpeed, WindSpeed));

            GameObject sphere = CreateProp(PrimitiveType.Sphere, "Wake Sphere",
                new Vector3(PathRadius, 0f, 0f), Vector3.one * SphereSize, CoolMaterial(), root);
            var mover = Undo.AddComponent<WaterShowcaseMover>(sphere);
            mover.pathCenter = Vector3.zero;
            mover.pathRadius = PathRadius;
            mover.lapSeconds = LapSeconds;
            var interactor = Undo.AddComponent<WaterSphereInteractor>(sphere);
            SetSerializedFloat(interactor, SphereInteractorStrengthPath, WakeStrength);
        }

        static void BuildFoamStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(7f, 1f, 7f);
            const float WindSpeed = 11f;
            const float WaveScaleMeters = 14f;
            const float WaveAmplitudeScale = 7f;
            const float FoamGenRate = 1f;

            WaterVolume body = CreateWaterBody(ctx, root, "Foam Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.Foam = true;
            ConfigureBody(body,
                (WaterVolumePropertyPaths.WindSpeed, WindSpeed),
                (WaterVolumePropertyPaths.WaveScaleMeters, WaveScaleMeters),
                (WaterVolumePropertyPaths.WaveAmplitudeScale, WaveAmplitudeScale),
                (WaterVolumePropertyPaths.FoamGenRate, FoamGenRate));
        }

        static void BuildShorelineStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(20f, 3f, 20f);
            const float TerrainSize = 40f;
            const float TerrainHeight = 6f;
            const float TerrainBaseY = -3f;
            const float BeachSlopePower = 1.4f;   // deep flat -> beach rise across +Z
            const float BumpAmplitude = 0.03f;    // gentle Perlin relief so the shoreline meanders
            const float BumpFrequency = 5f;
            const float SurfAmplitude = 1.2f;
            const float WindSpeed = 5f;

            Terrain terrain = CreateTerrain(BeachTerrainAssetPath, "Beach Terrain", root,
                new Vector3(-TerrainSize / 2f, TerrainBaseY, -TerrainSize / 2f),
                new Vector3(TerrainSize, TerrainHeight, TerrainSize),
                (u, v) => Mathf.Pow(v, BeachSlopePower)
                          + BumpAmplitude * Mathf.PerlinNoise(u * BumpFrequency, v * BumpFrequency));

            WaterVolume body = CreateWaterBody(ctx, root, "Shore Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: true);
            WithSerialized(body, so =>
            {
                SetEnum(so, WaterVolumePropertyPaths.BodyType, (int)WaterVolume.WaterBodyType.Lake);
                SetBool(so, WaterVolumePropertyPaths.UseBedDepth, true);
                SetObject(so, WaterVolumePropertyPaths.BedTerrain, terrain);
                SetFloat(so, WaterVolumePropertyPaths.SurfAmplitude, SurfAmplitude);
                SetFloat(so, WaterVolumePropertyPaths.WindSpeed, WindSpeed);
            });
        }

        static void BuildCausticsStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(1.5f, 1f, 1.5f);
            const float WindSpeed = 1f;       // barely-moving surface keeps the caustics dancing gently
            const float SphereSize = 0.7f;
            var spherePosition = new Vector3(0.5f, 0.1f, 0.3f); // half-submerged occluder

            WaterVolume body = CreateWaterBody(ctx, root, "Caustic Pool", Vector3.zero, extent,
                primary: true, withPool: true, withGodRays: false, withFoamParticles: false);
            ConfigureBody(body, (WaterVolumePropertyPaths.WindSpeed, WindSpeed));

            GameObject sphere = CreateProp(PrimitiveType.Sphere, "Occluder Sphere",
                spherePosition, Vector3.one * SphereSize, WarmMaterial(), root);
            Undo.AddComponent<WaterInteractable>(sphere); // occluder pass draws registered interactables
            Undo.AddComponent<WaterMembership>(sphere);
        }

        static void BuildGodRaysStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(2f, 3f, 2f);

            WaterVolume body = CreateWaterBody(ctx, root, "God Ray Pool", Vector3.zero, extent,
                primary: true, withPool: true, withGodRays: true, withFoamParticles: false);
            body.WaterFog = true; // the shafts need participating fog to read
        }

        static void BuildUnderwaterStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(4f, 3f, 4f);
            const float WindSpeed = 4f; // waves tall enough to sweep the lens across the waterline

            WaterVolume body = CreateWaterBody(ctx, root, "Underwater Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.WaterFog = true;
            ConfigureBody(body, (WaterVolumePropertyPaths.WindSpeed, WindSpeed));
        }

        static void BuildScatteringStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(7f, 2f, 7f);
            const float WindSpeed = 7f;
            const float WaveAmplitudeScale = 6f; // tall crests for the sun to shine through

            WaterVolume body = CreateWaterBody(ctx, root, "Scatter Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            WithSerialized(body, so =>
            {
                SetBool(so, WaterVolumePropertyPaths.VolumeScatter, true);
                SetBool(so, WaterVolumePropertyPaths.CrestScatter, true);
                SetFloat(so, WaterVolumePropertyPaths.WindSpeed, WindSpeed);
                SetFloat(so, WaterVolumePropertyPaths.WaveAmplitudeScale, WaveAmplitudeScale);
            });
        }

        static void BuildReflectionsStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(1.5f, 1f, 1.5f);
            const float PondSpacing = 7f;  // centre-to-centre; leaves >1 m between 3 m footprints
            const float WindSpeed = 1.5f;  // near-mirror surface so the three modes compare cleanly

            WaterVolume sky = CreateReflectionPond(ctx, root, "Pond (sky only)",
                new Vector3(-PondSpacing, 0f, 0f), extent, primary: false, WindSpeed,
                useSsr: false, usePlanar: false);
            WaterVolume ssr = CreateReflectionPond(ctx, root, "Pond (screen-space)",
                Vector3.zero, extent, primary: true, WindSpeed,
                useSsr: true, usePlanar: false);
            WaterVolume planar = CreateReflectionPond(ctx, root, "Pond (planar mirror)",
                new Vector3(PondSpacing, 0f, 0f), extent, primary: false, WindSpeed,
                useSsr: false, usePlanar: true);

            CreateReflectionGate(root, sky.transform.position);
            CreateReflectionGate(root, ssr.transform.position);
            CreateReflectionGate(root, planar.transform.position);
        }

        static void BuildExclusionStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(3f, 1f, 3f);
            var roomCenter = new Vector3(0f, -0.1f, 0f);
            var roomSize = new Vector3(2.4f, 1.6f, 2.4f); // top pokes out; bottom stays in the water
            var pillarPosition = new Vector3(0f, -0.55f, 0f);
            var pillarScale = new Vector3(0.5f, 0.7f, 0.5f);

            CreateWaterBody(ctx, root, "Exclusion Pond", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);

            var room = NewUndoableGameObject("Dry Room");
            room.transform.SetParent(root);
            room.transform.position = roomCenter;
            var volume = Undo.AddComponent<WaterExclusionVolume>(room);
            volume.size = roomSize; // walls stay ON (default): a bare room must paint its own boundary

            CreateProp(PrimitiveType.Cube, "Dry Pillar", pillarPosition, pillarScale,
                       WarmMaterial(), room.transform);
        }

        static void BuildClarityStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(10f, 3f, 10f);
            const float TerrainSize = 20f;
            const float TerrainHeight = 4.2f;
            const float TerrainBaseY = -3.5f;
            const float ShelfRampStart = 0.15f; // smooth deep plateau -> shallow sandbar across +Z
            const float ShelfRampEnd = 0.9f;
            const float ClarityShallowDepth = 0.3f;
            const float ClarityDeepDepth = 5f;

            Terrain terrain = CreateTerrain(ShelfTerrainAssetPath, "Shelf Terrain", root,
                new Vector3(-TerrainSize / 2f, TerrainBaseY, -TerrainSize / 2f),
                new Vector3(TerrainSize, TerrainHeight, TerrainSize),
                (u, v) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ShelfRampStart, ShelfRampEnd, v)));

            WaterVolume body = CreateWaterBody(ctx, root, "Clarity Lake", Vector3.zero, extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            WithSerialized(body, so =>
            {
                SetEnum(so, WaterVolumePropertyPaths.BodyType, (int)WaterVolume.WaterBodyType.Lake);
                SetBool(so, WaterVolumePropertyPaths.UseBedDepth, true);
                SetObject(so, WaterVolumePropertyPaths.BedTerrain, terrain);
                SetBool(so, WaterVolumePropertyPaths.ClarityFromDepth, true);
                SetFloat(so, WaterVolumePropertyPaths.ClarityShallowDepth, ClarityShallowDepth);
                SetFloat(so, WaterVolumePropertyPaths.ClarityDeepDepth, ClarityDeepDepth);
                SetBool(so, WaterVolumePropertyPaths.SurfEnabled, false); // surf would upstage the gradient
            });
        }

        static void BuildMultiBodyStation(BuildContext ctx, Transform root)
        {
            var extent = new Vector3(4f, 1f, 4f);
            const float BodySpacing = 5.5f;   // centre offset; leaves a 3 m dry gap between footprints
            const float SlowMotionTimeScale = 0.15f;
            const float DripInterval = 2f;    // same beat on both ponds makes the time gap obvious

            WaterVolume normal = CreateWaterBody(ctx, root, "Pond (normal time)",
                new Vector3(-BodySpacing, 0f, 0f), extent,
                primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            WaterVolume slow = CreateWaterBody(ctx, root, "Pond (slow motion)",
                new Vector3(BodySpacing, 0f, 0f), extent,
                primary: false, withPool: false, withGodRays: false, withFoamParticles: false);
            slow.TimeScale = SlowMotionTimeScale;

            // Calm surfaces: waves would intermittently fail the drippers' near-surface gate and
            // break the shared beat that makes the time-scale difference readable.
            normal.WindWaves = false;
            slow.WindWaves = false;

            CreateDripper(root, normal.transform.position, DripInterval);
            CreateDripper(root, slow.transform.position, DripInterval);
        }

        static void BuildChunkStation(BuildContext ctx, Transform root)
        {
            var seaExtent = new Vector3(24f, 1f, 24f);
            var chunkCenter = new Vector3(0f, 6f, 0f);
            const float ChunkRadius = 4f;
            const float FillCycleSeconds = 12f;

            CreateWaterBody(ctx, root, "Sea", Vector3.zero, seaExtent,
                primary: true, withPool: false, withGodRays: true, withFoamParticles: false);

            GameObject chunk = WaterChunkDemoBuilder.CreateSphereChunk(
                ctx, root, "Water Chunk (Sphere)", chunkCenter, ChunkRadius);

            var animator = Undo.AddComponent<WaterChunkFillAnimator>(root.gameObject);
            animator.chunk = chunk.GetComponentInChildren<WaterVolume>(true);
            animator.cycleSeconds = FillCycleSeconds;
        }

        // ---------------------------------------------------------------- station helpers
        static WaterVolume CreateReflectionPond(BuildContext ctx, Transform root, string name,
            Vector3 position, Vector3 extent, bool primary, float windSpeed, bool useSsr, bool usePlanar)
        {
            WaterVolume body = CreateWaterBody(ctx, root, name, position, extent,
                primary: primary, withPool: false, withGodRays: false, withFoamParticles: false);
            WithSerialized(body, so =>
            {
                SetBool(so, WaterVolumePropertyPaths.ScreenSpaceReflection, useSsr);
                SetBool(so, WaterVolumePropertyPaths.PlanarReflection, usePlanar);
                SetFloat(so, WaterVolumePropertyPaths.WindSpeed, windSpeed);
            });
            return body;
        }

        // A simple bright gate behind a pond - something above the surface for it to reflect.
        static void CreateReflectionGate(Transform root, Vector3 pondCenter)
        {
            const float GateOffsetZ = 2.2f;
            const float PillarSpacing = 1f;
            const float PillarHeight = 1.6f;
            var pillarScale = new Vector3(0.3f, PillarHeight, 0.3f);
            var lintelScale = new Vector3(2.4f, 0.3f, 0.3f);

            Vector3 gateBase = pondCenter + new Vector3(0f, 0f, GateOffsetZ);
            Material material = LightMaterial();
            CreateProp(PrimitiveType.Cube, "Gate Pillar",
                gateBase + new Vector3(-PillarSpacing, PillarHeight / 2f, 0f), pillarScale, material, root);
            CreateProp(PrimitiveType.Cube, "Gate Pillar",
                gateBase + new Vector3(PillarSpacing, PillarHeight / 2f, 0f), pillarScale, material, root);
            CreateProp(PrimitiveType.Cube, "Gate Lintel",
                gateBase + new Vector3(0f, PillarHeight, 0f), lintelScale, material, root);
        }

        static void CreateFloater(Transform root, PrimitiveType shape, string name, Vector3 position,
                                  float size, float buoyancy, Material material)
        {
            GameObject prop = CreateProp(shape, name, position, Vector3.one * size, material, root);
            Undo.AddComponent<Rigidbody>(prop);
            Undo.AddComponent<WaterInteractable>(prop);
            var buoy = Undo.AddComponent<WaterBuoyancy>(prop);
            buoy.buoyancy = buoyancy;
            buoy.waterLinearDamping = FloaterLinearDamping;
            buoy.waterAngularDamping = FloaterAngularDamping;
            Undo.AddComponent<WaterSplash>(prop);
            Undo.AddComponent<WaterMembership>(prop);
        }

        static void CreateDripper(Transform root, Vector3 waterlinePosition, float intervalSeconds)
        {
            var go = NewUndoableGameObject("Dripper");
            go.transform.SetParent(root);
            go.transform.position = waterlinePosition; // at the rest surface: passes the near-surface gate
            Undo.AddComponent<WaterRippleEmitter>(go);  // defaults: small drop, near-surface gated
            var dripper = Undo.AddComponent<WaterShowcaseDripper>(go);
            dripper.intervalSeconds = intervalSeconds;
            CreateProp(PrimitiveType.Sphere, "Dripper Visual",
                waterlinePosition + Vector3.up * DripperVisualHeight,
                Vector3.one * DripperVisualSize, CoolMaterial(), go.transform, keepCollider: false);
        }

        // Primitive prop with a create-once tinted material. Colliders stay unless the prop is
        // purely decorative (a collider inside the water would register as an obstacle footprint).
        static GameObject CreateProp(PrimitiveType type, string name, Vector3 position, Vector3 scale,
                                     Material material, Transform parent, bool keepCollider = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = scale;
            if (material != null) go.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!keepCollider)
            {
                var collider = go.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
            }
            return go;
        }

        static Material WarmMaterial() => PropMaterial(WarmPropMaterialPath, WarmPropColor);
        static Material CoolMaterial() => PropMaterial(CoolPropMaterialPath, CoolPropColor);
        static Material LightMaterial() => PropMaterial(LightPropMaterialPath, LightPropColor);

        static Material PropMaterial(string path, Color color)
        {
            Shader shader = DefaultPipelineMaterial().shader;
            return LoadOrCreateMaterial(path, shader, m => m.color = color);
        }

        // Create-once terrain (delete the asset to regenerate). heightmapResolution is set BEFORE
        // size on purpose: Unity rescales existing heights when the resolution changes afterwards.
        static Terrain CreateTerrain(string assetPath, string name, Transform parent, Vector3 origin,
                                     Vector3 size, Func<float, float, float> normalizedHeight)
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath);
            if (data == null)
            {
                data = new TerrainData { heightmapResolution = TerrainHeightmapResolution, size = size };
                var heights = new float[TerrainHeightmapResolution, TerrainHeightmapResolution];
                for (int z = 0; z < TerrainHeightmapResolution; z++)
                    for (int x = 0; x < TerrainHeightmapResolution; x++)
                    {
                        float u = x / (float)(TerrainHeightmapResolution - 1);
                        float v = z / (float)(TerrainHeightmapResolution - 1);
                        heights[z, x] = Mathf.Clamp01(normalizedHeight(u, v));
                    }
                data.SetHeights(0, 0, heights);
                AssetDatabase.CreateAsset(data, assetPath);
            }

            GameObject terrainGO = Terrain.CreateTerrainGameObject(data);
            terrainGO.name = name;
            Undo.RegisterCreatedObjectUndo(terrainGO, name);
            terrainGO.transform.SetParent(parent);
            terrainGO.transform.position = origin;

            var terrain = terrainGO.GetComponent<Terrain>();
            // URP renders the built-in default terrain material magenta; use the pipeline's own.
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultTerrainMaterial != null)
                terrain.materialTemplate = pipeline.defaultTerrainMaterial;
            return terrain;
        }

        // ---------------------------------------------------------------- serialized access
        // Private serialized blocks are configured the same way the Water Wizard does: through
        // SerializedObject + the shared WaterVolumePropertyPaths registry. Missing paths fail loudly.
        static void WithSerialized(WaterVolume body, Action<SerializedObject> edit)
        {
            var serialized = new SerializedObject(body);
            edit(serialized);
            serialized.ApplyModifiedProperties(); // rides the surrounding build undo group
        }

        // One-liner for the common "just floats" case.
        static void ConfigureBody(WaterVolume body, params (string path, float value)[] floats)
        {
            WithSerialized(body, so =>
            {
                foreach ((string path, float value) in floats) SetFloat(so, path, value);
            });
        }

        static void SetSerializedFloat(UnityEngine.Object target, string path, float value)
        {
            var serialized = new SerializedObject(target);
            SetFloat(serialized, path, value);
            serialized.ApplyModifiedProperties();
        }

        static SerializedProperty RequireProperty(SerializedObject so, string path)
        {
            SerializedProperty property = so.FindProperty(path);
            if (property == null)
                Debug.LogError($"[WebGpuWater] Serialized path '{path}' not found on {so.targetObject} - " +
                               "was a field renamed without updating WaterVolumePropertyPaths?");
            return property;
        }

        static void SetFloat(SerializedObject so, string path, float value)
        {
            SerializedProperty property = RequireProperty(so, path);
            if (property != null) property.floatValue = value;
        }

        static void SetBool(SerializedObject so, string path, bool value)
        {
            SerializedProperty property = RequireProperty(so, path);
            if (property != null) property.boolValue = value;
        }

        static void SetEnum(SerializedObject so, string path, int value)
        {
            SerializedProperty property = RequireProperty(so, path);
            if (property != null) property.enumValueIndex = value;
        }

        static void SetObject(SerializedObject so, string path, UnityEngine.Object value)
        {
            SerializedProperty property = RequireProperty(so, path);
            if (property != null) property.objectReferenceValue = value;
        }
    }
}
