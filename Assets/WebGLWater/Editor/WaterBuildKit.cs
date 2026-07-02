// WebGL Water - shared build kit (Unity 6 / URP port)
// Editor-only generators shared by the one-click scene builder and the demo builder:
// meshes, procedural sky/tiles, materials, camera/sun/splash rigging, a fully-wired
// water body, and the demo props (terrain bed, static obstacle, floating props). Kept
// in one place so both builders compose the same primitives instead of duplicating them.
using System.IO;
using UnityEditor;
using UnityEngine;
using WebGLWater;

namespace WebGLWater.EditorTools
{
    // The water shaders + compute, loaded and validated once (see WaterBuildKit.TryLoadShaders).
    internal struct ShaderSet
    {
        public Shader Water, Pool, Caustics, Obstacle, Receiver;
        public ComputeShader Compute;
    }

    // Shared assets built once per scene build and threaded through the body/prop creators, so
    // several water bodies reuse one grid/sky/material set (each body still instances its own
    // surface material at runtime, so sharing the asset is safe).
    internal sealed class BuildContext
    {
        public ShaderSet Shaders;
        public Mesh Grid;
        public Mesh PoolMesh;
        public Cubemap Sky;
        public Texture2D Tiles;
        public WaterQuality Quality;
        public Camera Camera;
        public OrbitCamera Orbit;
        public Light Sun;
        public WaterSplashEmitter Splash;
        public Material MatAbove, MatUnder, MatPool;
        public string Folder; // per-build asset folder for this scene's materials
    }

    internal static class WaterBuildKit
    {
        internal const string Root = "Assets/WebGLWater";
        internal const string Gen = "Assets/WebGLWater/Generated";
        internal const int GridDetail = 200;
        internal const int SkyCubemapSize = 128;

        // Shader names (keep in sync with the Shader "..." declarations in Shaders/).
        internal const string ShaderWaterSurface = "WebGLWater/WaterSurface";
        internal const string ShaderPoolWall = "WebGLWater/PoolWall";
        internal const string ShaderCaustics = "WebGLWater/Caustics";
        internal const string ShaderObstacle = "WebGLWater/ObstacleDepth";
        internal const string ShaderReceiver = "WebGLWater/WaterReceiver";
        internal const string ShaderGodRays = "WebGLWater/GodRays";

        // Material property names (keep in sync with the shader Properties blocks).
        internal const string PropUnderwater = "_Underwater";
        internal const string PropCull = "_Cull";
        internal const string PropBaseColor = "_BaseColor";
        internal const string PropRealRefraction = "_RealRefraction";
        internal const string KeywordRealRefraction = "_REAL_REFRACTION";
        internal const string PropGodRayColor = "_GodRayColor";
        internal const string PropFoamTex = "_FoamTex";
        internal const string PropFoamTexFrames = "_FoamTexFrames";
        internal const string PropFoamNormalTex = "_FoamNormalTex";
        internal const string PropParticleTex = "_ParticleTex";

        // GPU foam particles (compute + procedural-quad shader + sprite atlas).
        internal const string ShaderFoamParticles = "WebGLWater/FoamParticles";
        internal const string FoamParticleComputePath = Root + "/Shaders/WaterFoamParticles.compute";
        internal const string FoamParticleAtlasPath = Gen + "/FoamParticleAtlas_2x2.png";

        // Shuriken splash rendering (lit + soft-fade replacement for Sprites/Default).
        internal const string ShaderSplashParticles = "WebGLWater/SplashParticles";
        internal const string SplashDropletMaterialPath = Gen + "/SplashDroplet.mat";
        internal const string SplashCrownMaterialPath = Gen + "/SplashCrown.mat";
        internal const string SplashCrownSheetPath = Gen + "/SplashFlipbook_8x8.png";
        internal const string DropletTexturePath = Gen + "/Droplet.png";

        // Foam pattern flipbook (frames laid out in a grid; the surface shader
        // cross-fades frames over time so the foam churns internally) and its
        // frame-matched relief normal map (raw-RGB encoded, imported linear).
        const string FoamFlipbookPath = Gen + "/FoamFlipbook_4x4.png";
        const string FoamNormalFlipbookPath = Gen + "/FoamFlipbookNormal_4x4.png";
        const int FoamFlipbookCols = 4;
        const int FoamFlipbookRows = 4;

        // Cooler, more underwater-blue god rays than the shader's warm default (1.0, 0.97, 0.85).
        static readonly Color DefaultGodRayColor = new Color(0.70f, 0.85f, 1.0f, 1f);

        internal static void EnsureGenFolder() => EnsureFolder(Gen);

        // Create an asset folder (and any missing parents) if it doesn't exist yet.
        internal static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder) || AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            string leaf = Path.GetFileName(assetFolder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ---------------------------------------------------------------- context
        // Build the shared assets and scene rig for a build. Materials go into 'assetFolder' (one
        // folder per scene) so building or rebuilding one scene never overwrites another's tuned
        // materials. Shared deterministic assets (meshes, sky, tiles, quality) stay in Generated.
        // Returns false (with a dialog) when a required shader is missing, so callers can abort.
        internal static bool CreateContext(Transform sceneRoot, out BuildContext ctx, string assetFolder,
                                           bool buildPoolMaterial = true)
        {
            ctx = null;
            EnsureGenFolder();
            EnsureFolder(assetFolder);
            if (!TryLoadShaders(out ShaderSet shaders)) return false;

            var grid = SaveAsset(BuildGrid(GridDetail), Gen + "/WaterGrid.asset");
            var poolMesh = SaveAsset(BuildPool(), Gen + "/Pool.asset");
            var sky = SaveCubemap(BuildSky(SkyCubemapSize), Gen + "/SkyCubemap.cubemap");
            var tiles = LoadOrBuildTiles(Gen + "/Tiles.png");
            var quality = LoadOrCreateWaterQuality(Gen + "/WaterQuality.asset");
            var (matAbove, matUnder, matPool) = CreateWaterMaterials(shaders.Water, shaders.Pool, buildPoolMaterial, assetFolder);

            var cam = SetUpCamera(out OrbitCamera orbit);
            var sun = CreateSun(sceneRoot);
            var splash = CreateSplashEmitter(sceneRoot);

            ctx = new BuildContext
            {
                Shaders = shaders,
                Grid = grid,
                PoolMesh = poolMesh,
                Sky = sky,
                Tiles = tiles,
                Quality = quality,
                Camera = cam,
                Orbit = orbit,
                Sun = sun,
                Splash = splash,
                MatAbove = matAbove,
                MatUnder = matUnder,
                MatPool = matPool,
                Folder = assetFolder
            };
            return true;
        }

        // A fully-wired water body: a "Frame" GameObject carrying the WaterVolume (its transform IS
        // the volume frame - move/rotate it to place the water; volumeExtent sizes it) plus the
        // surface renderers (and optional analytic pool + god-ray volume) at world identity, which
        // the volume frame places in the shader. Only ONE body per scene should be primary.
        internal static WaterVolume CreateWaterBody(BuildContext ctx, Transform parent, string name,
            Vector3 position, Vector3 extent, bool primary, bool withPool, bool withGodRays)
        {
            var bodyRoot = new GameObject(name);
            bodyRoot.transform.SetParent(parent);

            var frameGO = new GameObject("Frame (WaterVolume)");
            frameGO.transform.SetParent(bodyRoot.transform);
            frameGO.transform.position = position;

            var volume = frameGO.AddComponent<WaterVolume>();
            volume.simCompute = ctx.Shaders.Compute;
            volume.causticsShader = ctx.Shaders.Caustics;
            volume.obstacleShader = ctx.Shaders.Obstacle;
            volume.waterMesh = ctx.Grid;
            volume.targetCamera = ctx.Camera;
            volume.sun = ctx.Sun;
            volume.orbit = ctx.Orbit;
            volume.splashEmitter = ctx.Splash;
            volume.tiles = ctx.Tiles;
            volume.sky = ctx.Sky;
            volume.quality = ctx.Quality;
            volume.volumeExtent = extent;
            volume.isPrimary = primary;

            // Renderers at world identity; the shader places the pool-space meshes via the frame.
            var rendGO = new GameObject("Renderers");
            rendGO.transform.SetParent(bodyRoot.transform);

            var above = CreateRenderer("Water (above)", ctx.Grid, ctx.MatAbove, rendGO.transform);
            var under = CreateRenderer("Water (under)", ctx.Grid, ctx.MatUnder, rendGO.transform);
            volume.surfaceAbove = above.GetComponent<Renderer>();
            volume.surfaceUnder = under.GetComponent<Renderer>();

            if (withPool && ctx.MatPool != null)
            {
                var poolGO = CreateRenderer("Analytic Pool", ctx.PoolMesh, ctx.MatPool, rendGO.transform);
                poolGO.GetComponent<MeshRenderer>().receiveShadows = true; // catch object shadows
                volume.poolRenderer = poolGO.GetComponent<Renderer>();
            }
            if (withGodRays)
            {
                var godGO = CreateGodRays(rendGO.transform, ctx.Folder);
                if (godGO != null) volume.godRayRenderer = godGO.GetComponent<Renderer>();
            }

            AddFoamParticles(volume, ctx.Folder);

            EditorUtility.SetDirty(volume);
            return volume;
        }

        // GPU foam/spray particles alongside the body's WaterVolume. The component idles
        // until the body's foam toggle is on, so bodies without foam pay nothing. Skipped
        // silently when the compute/shader/atlas assets are missing (feature simply absent).
        internal static WaterFoamParticles AddFoamParticles(WaterVolume volume, string materialFolder)
        {
            if (volume == null) return null;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(FoamParticleComputePath);
            var shader = Shader.Find(ShaderFoamParticles);
            if (compute == null || shader == null)
            {
                Debug.LogWarning("WebGL Water: foam particle compute/shader missing; skipping particle setup.");
                return null;
            }

            var material = LoadOrCreateMaterial(materialFolder + "/FoamParticles.mat", shader, m =>
            {
                var atlas = LoadFlipbook(FoamParticleAtlasPath, TextureWrapMode.Clamp, true);
                if (atlas != null) m.SetTexture(PropParticleTex, atlas);
            });

            var particles = volume.gameObject.AddComponent<WaterFoamParticles>();
            particles.volume = volume;
            particles.particleCompute = compute;
            particles.particleMaterial = material;
            EditorUtility.SetDirty(particles);
            return particles;
        }

        // ---------------------------------------------------------------- demo props
        // A procedural bowl terrain to act as a lake bed: deep in the middle, rising to a rim that
        // pokes above the water so there is a real shoreline. Sized/positioned so the bowl bottom
        // sits at the pool floor (surfaceY - extentY) and the rim rises just above the surface.
        internal static Terrain CreateProceduralTerrain(BuildContext ctx, Transform parent, Vector3 waterCenter,
            float horizontalExtent, float depth, string assetName = "DemoTerrain")
        {
            const int HeightmapResolution = 129;   // 2^n + 1
            const float RimHeightFactor = 1.4f;    // terrain height as a multiple of the water depth
            const float BowlRimFraction = 0.7f;    // normalised bowl height reached at the rim
            const float NoiseFrequency = 6f;
            const float NoiseAmplitude = 0.1f;

            float worldSize = 2f * horizontalExtent;
            float terrainHeight = depth * RimHeightFactor;

            var data = new TerrainData { heightmapResolution = HeightmapResolution };
            data.size = new Vector3(worldSize, terrainHeight, worldSize);

            var heights = new float[HeightmapResolution, HeightmapResolution];
            for (int z = 0; z < HeightmapResolution; z++)
                for (int x = 0; x < HeightmapResolution; x++)
                {
                    float u = x / (float)(HeightmapResolution - 1);
                    float v = z / (float)(HeightmapResolution - 1);
                    float cx = u * 2f - 1f, cz = v * 2f - 1f;
                    float radius = Mathf.Clamp01(Mathf.Sqrt(cx * cx + cz * cz));
                    float bowl = Mathf.SmoothStep(0f, 1f, radius) * BowlRimFraction;
                    float noise = Mathf.PerlinNoise(u * NoiseFrequency, v * NoiseFrequency) * NoiseAmplitude;
                    heights[z, x] = Mathf.Clamp01(bowl + noise);
                }
            data.SetHeights(0, 0, heights);
            data = SaveTerrainData(data, ctx.Folder + "/" + assetName + ".asset");

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Lake Bed (terrain)";
            go.transform.SetParent(parent);
            // Centre the terrain under the water and drop its base a full depth below the surface.
            go.transform.position = new Vector3(waterCenter.x - horizontalExtent,
                                                waterCenter.y - depth,
                                                waterCenter.z - horizontalExtent);
            return go.GetComponent<Terrain>();
        }

        // A non-moving obstacle that still displaces the water (WaterInteractable, no Rigidbody) and
        // is lit by the lake it sits in (receiver material + membership).
        internal static GameObject CreateStaticObstacle(BuildContext ctx, Transform parent,
            Vector3 position, Vector3 scale, string matName = "ObstacleStatic")
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Static Obstacle";
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = scale;
            ApplyReceiverMaterial(go, ctx, new Color(0.55f, 0.55f, 0.60f), matName);
            go.AddComponent<WaterInteractable>();  // displaces the surface; no Rigidbody -> stays put
            go.AddComponent<WaterMembership>();
            return go;
        }

        // A buoyant prop: floats, displaces, splashes, and is lit by its lake.
        internal static GameObject CreateFloatingProp(BuildContext ctx, Transform parent,
            PrimitiveType prim, Vector3 position, float scale, Color color, string matName, float mass = 0.4f)
        {
            var go = GameObject.CreatePrimitive(prim);
            go.name = "Floating Prop";
            go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            ApplyReceiverMaterial(go, ctx, color, matName);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            go.AddComponent<WaterInteractable>();
            go.AddComponent<WaterBuoyancy>();
            go.AddComponent<WaterSplash>();
            go.AddComponent<WaterMembership>();
            return go;
        }

        // A thin box collider under the water so sinking props have something to rest on.
        internal static GameObject CreateFloorCollider(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("Floor Collider");
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        static void ApplyReceiverMaterial(GameObject go, BuildContext ctx, Color baseColor, string matName)
        {
            if (ctx.Shaders.Receiver == null) return;
            // Create-once into the scene's own folder so it persists (a transient new Material
            // serializes to null -> magenta) and is never clobbered by another scene's build.
            var mat = LoadOrCreateMaterial(ctx.Folder + "/" + matName + ".mat", ctx.Shaders.Receiver,
                                           m => m.SetColor(PropBaseColor, baseColor));
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ---------------------------------------------------------------- materials
        // The above-water pass culls BACK faces; the underwater pass culls FRONT faces (inverted
        // from the shader's own defaults, which reads better here). The pool interior culls back
        // faces (_Cull maps to UnityEngine.Rendering.CullMode). Both surface materials enable REAL
        // screen-space refraction by default, so the water is transparent without hand-tweaking
        // (needs Opaque Texture + Depth Texture on the active URP asset).
        internal static (Material above, Material under, Material pool) CreateWaterMaterials(
            Shader sfWater, Shader sfPool, bool buildAnalyticPool, string folder)
        {
            float cullFront = (float)UnityEngine.Rendering.CullMode.Front;
            float cullBack = (float)UnityEngine.Rendering.CullMode.Back;
            var above = LoadOrCreateMaterial(folder + "/WaterAbove.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 0f); m.SetFloat(PropCull, cullBack); EnableRealRefraction(m); AssignFoamFlipbook(m); });
            var under = LoadOrCreateMaterial(folder + "/WaterUnder.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 1f); m.SetFloat(PropCull, cullFront); EnableRealRefraction(m); });
            Material pool = null;
            if (buildAnalyticPool && sfPool != null)
                pool = LoadOrCreateMaterial(folder + "/Pool.mat", sfPool, m => m.SetFloat(PropCull, cullBack));
            return (above, under, pool);
        }

        // Turn on the surface shader's real (screen-space) refraction toggle: set the property AND
        // the linked shader keyword, since setting the float alone doesn't flip the keyword.
        static void EnableRealRefraction(Material m)
        {
            m.SetFloat(PropRealRefraction, 1f);
            m.EnableKeyword(KeywordRealRefraction);
        }

        // Give a water surface material the animated foam pattern + its relief normal
        // map. Skipped silently when the flipbook asset is absent: the shader's white/bump
        // defaults degrade to flat foam.
        internal static void AssignFoamFlipbook(Material m)
        {
            var flipbook = LoadFlipbook(FoamFlipbookPath, TextureWrapMode.Repeat, true);
            if (flipbook == null) return;
            m.SetTexture(PropFoamTex, flipbook);
            m.SetVector(PropFoamTexFrames, new Vector4(FoamFlipbookCols, FoamFlipbookRows, 0f, 0f));

            var relief = LoadFlipbook(FoamNormalFlipbookPath, TextureWrapMode.Repeat, true, linear: true);
            if (relief != null) m.SetTexture(PropFoamNormalTex, relief);
        }

        // Underwater god-ray volume (caustic-masked light shafts). Returns null if the shader is
        // missing (the feature is simply absent then).
        internal static GameObject CreateGodRays(Transform parent, string folder)
        {
            var sfGodRays = Shader.Find(ShaderGodRays);
            if (sfGodRays == null) return null;

            var godRayMat = LoadOrCreateMaterial(folder + "/GodRays.mat", sfGodRays,
                                                 m => m.SetColor(PropGodRayColor, DefaultGodRayColor));
            var go = CreateRenderer("God Rays", SaveAsset(BuildGodRayBox(), Gen + "/GodRayBox.asset"),
                                    godRayMat, parent);
            var gmr = go.GetComponent<MeshRenderer>();
            gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gmr.receiveShadows = false;
            return go;
        }

        // ---------------------------------------------------------------- scene rig
        // Reuse the scene's main camera if there is one (avoids two cameras rendering on top of each
        // other), then attach the orbit + planar-reflection helpers.
        internal static Camera SetUpCamera(out OrbitCamera orbit)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = new GameObject("Water Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = "MainCamera";
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = cam.gameObject.AddComponent<OrbitCamera>();
            orbit.pivot = new Vector3(0f, -0.5f, 0f);
            orbit.pitch = -25f;
            orbit.yaw = -200.5f;
            orbit.distance = 4f;

            var planar = cam.GetComponent<PlanarReflection>();
            if (planar == null) planar = cam.gameObject.AddComponent<PlanarReflection>();
            planar.sourceCamera = cam;
            planar.waterHeight = 0f;
            planar.enableReflection = false;
            return cam;
        }

        // Single directional light: drives the analytic water + caustics (via the _LightDir global
        // the controller publishes) AND casts real URP shadows.
        internal static Light CreateSun(Transform parent)
        {
            var sunGO = new GameObject("Sun");
            sunGO.transform.SetParent(parent);
            var sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.LookRotation(-new Vector3(2f, 2f, -1f).normalized);
            return sun;
        }

        // Shared, fully editable splash particles (drift droplets + a flipbook crown).
        // Materials are create-once assets on the lit splash shader, so hand-tuning
        // survives rebuilds (same convention as the water/foam-particle materials).
        internal static WaterSplashEmitter CreateSplashEmitter(Transform parent)
        {
            var splashGO = new GameObject("Splash Particles");
            splashGO.transform.SetParent(parent);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            var splashPSR = splashGO.GetComponent<ParticleSystemRenderer>();
            splashPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            splashPSR.renderMode = ParticleSystemRenderMode.Billboard;
            var splashEmitter = splashGO.AddComponent<WaterSplashEmitter>();
            splashEmitter.particles = splashPS;

            var crownGO = new GameObject("Splash Crown");
            crownGO.transform.SetParent(parent);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, 8, 8);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);
            crownPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashCrownMaterialPath, LoadFlipbook(SplashCrownSheetPath, TextureWrapMode.Clamp, false));
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;
        }

        // Upgrade (or create) both shared splash materials on the lit shader. They are
        // referenced by every demo scene, so this fixes all of them at once.
        internal static void UpgradeSplashMaterials()
        {
            EnsureGenFolder();
            LoadOrCreateSplashMaterial(SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            LoadOrCreateSplashMaterial(SplashCrownMaterialPath, LoadFlipbook(SplashCrownSheetPath, TextureWrapMode.Clamp, false));
            AssetDatabase.SaveAssets();
        }

        // A splash material on the lit shader (create-once). Also the one-click upgrade
        // path for materials created before the lit shader existed: an existing material
        // still on another shader is switched in place, keeping its texture.
        static Material LoadOrCreateSplashMaterial(string path, Texture2D sprite)
        {
            var shader = Shader.Find(ShaderSplashParticles);
            if (shader == null)
            {
                Debug.LogWarning($"WebGL Water: shader '{ShaderSplashParticles}' missing; splash material not created.");
                return null;
            }

            var material = LoadOrCreateMaterial(path, shader, m =>
            {
                if (sprite != null) m.mainTexture = sprite;
            });
            if (material.shader != shader)
            {
                material.shader = shader; // upgrade in place; _MainTex carries over by name
                if (material.mainTexture == null && sprite != null) material.mainTexture = sprite;
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        // ---------------------------------------------------------------- meshes
        internal static GameObject CreateRenderer(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        // XY-plane grid in [-1,1], z = 0 (matches the original lightgl plane mesh).
        internal static Mesh BuildGrid(int detail)
        {
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
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return mesh;
        }

        // Open-top box: floor at y=-1, walls up to y=2/12, spanning x,z in [-1,1]. Faces inward.
        internal static Mesh BuildPool()
        {
            const float top = 2f / 12f;
            const float lo = -1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, lo, -1), new Vector3(-1, lo, 1), new Vector3(1, lo, 1), new Vector3(1, lo, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, top, -1), new Vector3(-1, top, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(-1, lo, 1), new Vector3(-1, top, 1), new Vector3(1, top, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, lo, -1), new Vector3(-1, top, -1), new Vector3(-1, top, 1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(1, top, 1), new Vector3(1, top, -1));

            var mesh = new Mesh { name = "Pool" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return mesh;
        }

        // Closed box in POOL space: y in [-1,0], x,z in [-1,1]. Outward-wound (like a primitive
        // cube) so the GodRays pass's Cull Front renders the back faces.
        internal static Mesh BuildGodRayBox()
        {
            const float lo = -1f, hi = 0f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, hi, -1), new Vector3(-1, hi, 1), new Vector3(1, hi, 1), new Vector3(1, hi, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, -1), new Vector3(-1, hi, -1), new Vector3(1, hi, -1), new Vector3(1, lo, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(1, hi, 1), new Vector3(-1, hi, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, hi, 1), new Vector3(-1, hi, -1), new Vector3(-1, lo, -1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, hi, -1), new Vector3(1, hi, 1), new Vector3(1, lo, 1));

            var mesh = new Mesh { name = "GodRayBox" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return mesh;
        }

        // ---------------------------------------------------------------- textures
        internal static Cubemap BuildSky(int size)
        {
            var cube = new Cubemap(size, TextureFormat.RGB24, false);
            CubemapFace[] faces = {
                CubemapFace.PositiveX, CubemapFace.NegativeX,
                CubemapFace.PositiveY, CubemapFace.NegativeY,
                CubemapFace.PositiveZ, CubemapFace.NegativeZ
            };
            foreach (var face in faces)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float w = (y + 0.5f) / size * 2f - 1f;
                        Vector3 dir = FaceDir(face, u, w).normalized;
                        cube.SetPixel(face, x, y, SkyColor(dir));
                    }
            cube.Apply();
            return cube;
        }

        static Vector3 FaceDir(CubemapFace f, float u, float v)
        {
            switch (f)
            {
                case CubemapFace.PositiveX: return new Vector3(1, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1);
                default: return new Vector3(-u, -v, -1);
            }
        }

        static Color SkyColor(Vector3 dir)
        {
            Color horizon = new Color(0.78f, 0.86f, 0.96f);
            Color zenith = new Color(0.26f, 0.47f, 0.86f);
            Color ground = new Color(0.30f, 0.30f, 0.33f);
            if (dir.y >= 0f) return Color.Lerp(horizon, zenith, Mathf.Pow(dir.y, 0.6f));
            return Color.Lerp(horizon, ground, Mathf.Pow(-dir.y, 0.5f));
        }

        internal static Texture2D LoadOrBuildTiles(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int s = 256;
            var tex = new Texture2D(s, s, TextureFormat.RGB24, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    int cell = 32;
                    bool grout = (x % cell < 2) || (y % cell < 2);
                    float n = 0.85f + 0.15f * Mathf.PerlinNoise(x * 0.08f, y * 0.08f);
                    Color baseCol = new Color(0.55f, 0.75f, 0.85f) * n;
                    tex.SetPixel(x, y, grout ? new Color(0.30f, 0.45f, 0.55f) : baseCol);
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D LoadOrBuildDroplet(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f;
                    float dy = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    a *= a;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // 'linear' is for data textures (e.g. the raw-RGB foam normal map): sRGB sampling
        // would distort the decoded vectors.
        static Texture2D LoadFlipbook(string path, TextureWrapMode wrap, bool mipmaps, bool linear = false)
        {
            if (!File.Exists(path)) return null;
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = !linear;
                imp.alphaIsTransparency = !linear;
                imp.wrapMode = wrap;
                imp.mipmapEnabled = mipmaps;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------------------------------------------------------------- helpers
        // Load + validate the water shaders. Fails fast (dialog + false) if a REQUIRED shader
        // (surface, caustics, compute) is missing; optional shaders only warn.
        internal static bool TryLoadShaders(out ShaderSet shaders)
        {
            shaders = new ShaderSet
            {
                Water = Shader.Find(ShaderWaterSurface),
                Pool = Shader.Find(ShaderPoolWall),
                Caustics = Shader.Find(ShaderCaustics),
                Obstacle = Shader.Find(ShaderObstacle),
                Receiver = Shader.Find(ShaderReceiver),
                Compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "/Shaders/WaterSim.compute")
            };

            if (shaders.Water == null || shaders.Caustics == null || shaders.Compute == null)
            {
                EditorUtility.DisplayDialog("WebGL Water",
                    "Could not find the shaders / compute shader. Make sure the WebGLWater/Shaders folder imported without errors, then try again.",
                    "OK");
                return false;
            }

            if (shaders.Obstacle == null) Debug.LogWarning($"[WebGL Water] Shader '{ShaderObstacle}' not found; object->water displacement will be disabled.");
            return true;
        }

        internal static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        internal static Material SaveMaterial(Material m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // Create-once: reuse the material already at 'path' (preserving any hand-tuning) instead of
        // overwriting it, so rebuilding a scene - or building a different one - never resets it.
        internal static Material LoadOrCreateMaterial(string path, Shader shader, System.Action<Material> configure = null)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var m = new Material(shader);
            configure?.Invoke(m);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        internal static Cubemap SaveCubemap(Cubemap c, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (existing != null) { EditorUtility.CopySerialized(c, existing); return existing; }
            AssetDatabase.CreateAsset(c, path);
            return c;
        }

        static TerrainData SaveTerrainData(TerrainData data, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (existing != null) { EditorUtility.CopySerialized(data, existing); return existing; }
            AssetDatabase.CreateAsset(data, path);
            return data;
        }

        internal static WaterQuality LoadOrCreateWaterQuality(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
            if (existing != null) return existing;
            var q = ScriptableObject.CreateInstance<WaterQuality>();
            AssetDatabase.CreateAsset(q, path);
            return q;
        }
    }
}
