// WebGL Water - main controller (Unity 6 / URP port)
// Drives the simulation, caustics, mouse interaction and the
// orbiting camera. Port of main.js / renderer.js by Evan Wallace (MIT).
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace WebGLWater
{
    [DefaultExecutionOrder(-50)]
    public class WaterController : MonoBehaviour
    {
        [Header("Assigned by the scene builder")]
        public ComputeShader simCompute;
        public Shader causticsShader;
        public Shader obstacleShader;     // WebGLWater/ObstacleDepth - footprint of interactable objects
        public Mesh waterMesh;            // XY grid plane, [-1,1], shared with the water surface renderers
        public Camera targetCamera;
        public Light sun;                 // directional light: drives water, caustics AND real shadows

        [Header("Look / surfaces")]
        public Texture tiles;             // pool tile albedo sampled by the water reflection (assign your own)
        public Cubemap sky;               // sky cubemap for above-water reflections

        [Header("Simulation")]
        [Tooltip("Direction TOWARD the light. Auto-driven from 'sun' when one is assigned.")]
        public Vector3 lightDir = new Vector3(2f, 2f, -1f);
        public int causticResolution = 1024;

        [Header("Object interaction")]
        [Tooltip("MASTER strength for how strongly submerged objects displace the water " +
                 "(height units, comparable to Ripple Strength). Per-object weighting is " +
                 "WaterInteractable.displaceScale (leave those at 1 for uniform objects).")]
        [Range(0f, 0.5f)] public float obstacleStrength = 0.08f;
        [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
        public bool obstacleFlipY = false;

        [Header("Water fog (Beer-Lambert)")]
        [Tooltip("Global depth absorption, shared by the surface, objects and pool.")]
        public bool waterFog = false;
        public Color fogColor = new Color(0.10f, 0.30f, 0.40f);
        [Tooltip("Per-channel extinction; red highest so it absorbs first.")]
        public Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Range(0f, 8f)] public float fogDensity = 2f;

        [Header("Foam")]
        public bool foam = false;
        [Tooltip("How fast turbulence creates foam.")]
        [Range(0f, 2f)] public float foamGenRate = 0.6f;
        [Tooltip("Foam survival per step. Lower = fades faster.")]
        [Range(0.80f, 1f)] public float foamDecay = 0.97f;
        [Tooltip("Diffusion of foam toward neighbours.")]
        [Range(0f, 1f)] public float foamSpread = 0.2f;
        [Tooltip("How far foam is carried along the surface flow each step (texels). 0 = old isotropic spread.")]
        [Range(0f, 8f)] public float foamAdvect = 3f;
        public float foamFromSpeed = 6f;
        public float foamFromCurvature = 30f;
        [Space]
        public Color foamColor = Color.white;
        [Range(0f, 2f)] public float foamStrength = 1f;
        [Tooltip("Width of the foam band along the pool walls (pool units).")]
        [Range(0f, 0.5f)] public float foamBorderWidth = 0.08f;
        [Tooltip("Depth band for contact foam where objects meet the waterline.")]
        [Range(0f, 0.5f)] public float foamContactDepth = 0.06f;

        [Header("Ripple tuning")]
        [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
        [Range(0.1f, 2.0f)] public float waveSpeed = 2.0f;
        [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
        [Range(0.90f, 1.0f)] public float damping = 0.995f;
        [Tooltip("Simulation sub-steps per frame. More = faster, smoother propagation.")]
        [Range(1, 8)] public int stepsPerFrame = 2;
        [Tooltip("Height added by a click/drag ripple (deformation intensity).")]
        [Range(0.001f, 0.08f)] public float rippleStrength = 0.01f;
        [Tooltip("Radius of a click/drag ripple in pool space.")]
        [Range(0.005f, 0.2f)] public float rippleRadius = 0.03f;
        [Tooltip("Seed the pool with random ripples on start.")]
        public bool seedRipplesOnStart = true;
        [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
        public bool conserveVolume = true;

        [Header("Camera")]
        public OrbitCamera orbit;

        [Header("Splash")]
        [Tooltip("Shared splash emitter used for mouse interaction (and objects).")]
        public WaterSplashEmitter splashEmitter;

        // runtime
        WaterSimulation _water;
        WaterObstacle _obstacle;

        // CPU copy of the height field for buoyancy queries
        Color[] _heightCpu;
        bool _heightReady, _readbackInFlight;
        const int SimRes = WaterSimulation.Resolution;
        Material _causticMat;
        RenderTexture _causticRT;
        RenderTexture _heightMip;
        CommandBuffer _cb;

        bool _paused;

        // interaction
        const int MODE_NONE = -1, MODE_ADD_DROPS = 0, MODE_ORBIT = 2;

        // Pool-space distance the cursor must travel between injected ripples while
        // dragging. Holding the button still otherwise re-injects into the same texels
        // every frame, pumping unbounded energy into the explicit solver until it
        // overshoots. The initial press bypasses this (via _forceDrop) so a plain click
        // still makes a ripple.
        const float MinDragRippleSpacing = 0.01f;

        int _mode = MODE_NONE;
        Vector2 _oldMouse;
        Vector3 _prevDrop;
        bool _forceDrop;

        // shader global ids
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = Shader.PropertyToID("_LightDir");
        static readonly int ID_SunColor = Shader.PropertyToID("_SunColor");
        static readonly int ID_FogColor = Shader.PropertyToID("_WaterFogColor");
        static readonly int ID_FogExt = Shader.PropertyToID("_WaterExtinction");
        static readonly int ID_FogDensity = Shader.PropertyToID("_WaterFogDensity");
        static readonly int ID_FogEnabled = Shader.PropertyToID("_WaterFogEnabled");
        static readonly int ID_FoamMask = Shader.PropertyToID("_FoamMask");
        static readonly int ID_FoamColor = Shader.PropertyToID("_FoamColor");
        static readonly int ID_FoamEnabled = Shader.PropertyToID("_FoamEnabled");
        static readonly int ID_FoamStrength = Shader.PropertyToID("_FoamStrength");
        static readonly int ID_FoamBorder = Shader.PropertyToID("_FoamBorderWidth");
        static readonly int ID_FoamContact = Shader.PropertyToID("_FoamContactDepth");

        void OnEnable()
        {
            if (simCompute == null) { Debug.LogError("WaterController: simCompute not assigned."); enabled = false; return; }

            _water = new WaterSimulation(simCompute);

            if (obstacleShader != null)
                _obstacle = new WaterObstacle(obstacleShader, WaterSimulation.Resolution, 0f);

            _causticMat = new Material(causticsShader);
            _causticRT = new RenderTexture(causticResolution, causticResolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex"
            };
            _causticRT.Create();
            _cb = new CommandBuffer { name = "WebGLWater.Caustics" };

            _heightMip = new RenderTexture(WaterSimulation.Resolution, WaterSimulation.Resolution, 0, RenderTextureFormat.RFloat)
            {
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "HeightMip"
            };
            _heightMip.Create();

            // seed the pool with a few ripples
            if (seedRipplesOnStart)
                for (int i = 0; i < 20; i++)
                    _water.AddDrop(Random.value * 2f - 1f, Random.value * 2f - 1f, 0.03f, (i & 1) == 1 ? 0.01f : -0.01f);

            if (targetCamera != null)
            {
                targetCamera.fieldOfView = 45f;
                targetCamera.nearClipPlane = 0.01f;
                targetCamera.farClipPlane = 100f;
            }

            if (sun != null) lightDir = -sun.transform.forward; // light travels along sun.forward
            Shader.SetGlobalVector(ID_Light, lightDir.normalized);
            Shader.SetGlobalColor(ID_SunColor, sun != null ? sun.color * sun.intensity : Color.white);
            PublishFog();
            if (tiles != null) Shader.SetGlobalTexture(ID_Tiles, tiles);
            if (sky != null) Shader.SetGlobalTexture(ID_Sky, sky);
        }

        void OnDisable()
        {
            _water?.Dispose();
            _obstacle?.Dispose();
            if (_causticRT != null) _causticRT.Release();
            if (_heightMip != null) _heightMip.Release();
            _cb?.Release();
        }

        void Update()
        {
            HandleKeys();
            HandleMouse();

            float dt = Time.deltaTime;
            if (!_paused) Step(dt);

            // publish globals for the surface / pool shaders
            if (sun != null) lightDir = -sun.transform.forward;
            Shader.SetGlobalTexture(ID_Water, _water.Texture);
            Shader.SetGlobalVector(ID_Light, lightDir.normalized);
            Shader.SetGlobalColor(ID_SunColor, sun != null ? sun.color * sun.intensity : Color.white);
            PublishFog();
            PublishFoam();

            UpdateCaustics();
            RequestHeightReadback();
        }

        // ---- height readback for buoyancy ----------------------------------
        void RequestHeightReadback()
        {
            if (_readbackInFlight || _water == null) return;
            if (!SystemInfo.supportsAsyncGPUReadback) return; // buoyancy degrades gracefully
            _readbackInFlight = true;
            AsyncGPUReadback.Request(_water.Texture, 0, TextureFormat.RGBAFloat, OnHeightReadback);
        }

        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            _readbackInFlight = false;
            if (req.hasError) return;
            var data = req.GetData<Color>();
            if (_heightCpu == null || _heightCpu.Length != data.Length)
                _heightCpu = new Color[data.Length];
            data.CopyTo(_heightCpu);
            _heightReady = true;
        }

        /// <summary>Inject a ripple at pool position (x,z in [-1,1]); used by splashes.</summary>
        public void AddRipple(float x, float z, float radius, float strength)
        {
            _water?.AddDrop(x, z, radius, strength);
        }

        /// <summary>World-space height (Y) of the water surface at pool position
        /// (x,z in [-1,1]). Returns false until the first readback has landed or if
        /// the point is outside the pool.</summary>
        public bool TryGetWaterHeight(float x, float z, out float height)
        {
            height = 0f;
            if (!_heightReady || _heightCpu == null) return false;
            float u = x * 0.5f + 0.5f, v = z * 0.5f + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            int px = Mathf.Clamp((int)(u * SimRes), 0, SimRes - 1);
            int pz = Mathf.Clamp((int)(v * SimRes), 0, SimRes - 1);
            height = _heightCpu[pz * SimRes + px].r; // surface sits at y = 0 + displacement
            return true;
        }

        /// <summary>World-space surface height plus the horizontal surface-flow
        /// direction at pool position (x,z in [-1,1]). Flow is the stored normal.xz,
        /// which equals -gradient(height) (i.e. the downhill direction the surface
        /// pushes floating things). Returns false until the first readback lands or if
        /// the point is outside the pool.</summary>
        public bool TryGetSurface(float x, float z, out float height, out Vector2 flow)
        {
            height = 0f;
            flow = Vector2.zero;
            if (!_heightReady || _heightCpu == null) return false;
            float u = x * 0.5f + 0.5f, v = z * 0.5f + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            int px = Mathf.Clamp((int)(u * SimRes), 0, SimRes - 1);
            int pz = Mathf.Clamp((int)(v * SimRes), 0, SimRes - 1);
            Color sample = _heightCpu[pz * SimRes + px];
            height = sample.r;
            flow = new Vector2(sample.b, sample.a); // (normal.x, normal.z)
            return true;
        }

        void Step(float seconds)
        {
            if (seconds > 1f) return;

            // Push the surface with the live submerged footprint of interactable objects.
            if (_obstacle != null)
            {
                _obstacle.Render(0f);
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr, obstacleStrength, obstacleFlipY);
            }

            int steps = Mathf.Max(1, stepsPerFrame);
            for (int i = 0; i < steps; i++)
                _water.StepSimulation(waveSpeed, damping);

            if (conserveVolume)
            {
                Graphics.Blit(_water.Texture, _heightMip); // copy height (R) into the mipped RT
                _heightMip.GenerateMips();                 // top 1x1 mip = mean height
                _water.ConserveVolume(_heightMip);         // subtract the mean
            }

            _water.UpdateNormals();

            if (foam)
                _water.StepFoam(foamGenRate, foamDecay, foamSpread, foamFromSpeed, foamFromCurvature, foamAdvect);
        }

        void UpdateCaustics()
        {
            _cb.Clear();
            _cb.SetRenderTarget(_causticRT);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _causticMat, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
            Shader.SetGlobalTexture(ID_Caustic, _causticRT);
        }

        void PublishFog()
        {
            Shader.SetGlobalColor(ID_FogColor, fogColor);
            Shader.SetGlobalColor(ID_FogExt, fogExtinction);
            Shader.SetGlobalFloat(ID_FogDensity, fogDensity);
            Shader.SetGlobalFloat(ID_FogEnabled, waterFog ? 1f : 0f);
        }

        void PublishFoam()
        {
            if (_water != null && _water.FoamTexture != null)
                Shader.SetGlobalTexture(ID_FoamMask, _water.FoamTexture);
            Shader.SetGlobalColor(ID_FoamColor, foamColor);
            Shader.SetGlobalFloat(ID_FoamEnabled, foam ? 1f : 0f);
            Shader.SetGlobalFloat(ID_FoamStrength, foamStrength);
            Shader.SetGlobalFloat(ID_FoamBorder, foamBorderWidth);
            Shader.SetGlobalFloat(ID_FoamContact, foamContactDepth);
        }

        // ---- camera ---------------------------------------------------------
        Ray PixelRay(Vector2 p)
        {
            return targetCamera.ScreenPointToRay(new Vector3(p.x, p.y, 0f));
        }

        // ---- interaction ----------------------------------------------------
        void HandleMouse()
        {
            // While pinching (2+ fingers), don't ripple/orbit — let the camera zoom.
            if (MultiTouch()) { _mode = MODE_NONE; return; }

            Vector2 m = MousePos();

            if (MouseDown())
            {
                _oldMouse = m;
                Ray ray = PixelRay(m);
                Vector3 eye = ray.origin;
                Vector3 d = ray.direction;

                Vector3 pointOnPlane = eye + d * (-eye.y / d.y); // intersect y = 0

                if (Mathf.Abs(pointOnPlane.x) < 1f && Mathf.Abs(pointOnPlane.z) < 1f)
                {
                    _mode = MODE_ADD_DROPS;
                    _prevDrop = pointOnPlane;
                    _forceDrop = true; // the initial press always injects one ripple
                    DuringDrag(m);
                }
                else
                {
                    _mode = MODE_ORBIT;
                }
            }
            else if (MouseHeld())
            {
                DuringDrag(m);
            }
            else if (MouseUp())
            {
                _mode = MODE_NONE;
            }
        }

        void DuringDrag(Vector2 m)
        {
            switch (_mode)
            {
                case MODE_ADD_DROPS:
                {
                    Ray ray = PixelRay(m);
                    Vector3 eye = ray.origin, d = ray.direction;
                    Vector3 p = eye + d * (-eye.y / d.y);

                    // Throttle injection by distance travelled so holding the cursor
                    // still doesn't pump energy into the same texels every frame.
                    float moved = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(_prevDrop.x, _prevDrop.z));
                    if (!_forceDrop && moved < MinDragRippleSpacing) break;
                    _forceDrop = false;

                    _water.AddDrop(p.x, p.z, rippleRadius, rippleStrength);

                    // splash droplets where the cursor drags across the surface
                    if (splashEmitter != null)
                    {
                        float strength = Mathf.Clamp01(moved / 0.08f);
                        if (strength > 0.1f)
                            splashEmitter.EmitSplash(new Vector3(p.x, 0f, p.z), strength * 0.6f, rippleRadius * 4f);
                    }
                    _prevDrop = p;
                    break;
                }
                case MODE_ORBIT:
                {
                    if (orbit != null) orbit.Rotate(m.x - _oldMouse.x, m.y - _oldMouse.y);
                    break;
                }
            }
            _oldMouse = m;
        }

        void HandleKeys()
        {
            if (KeySpaceDown()) _paused = !_paused;
            if (KeyLHeld() && targetCamera != null)
            {
                // Point the real sun along the camera view (or the fallback vector).
                if (sun != null)
                    sun.transform.rotation = Quaternion.LookRotation(targetCamera.transform.forward);
                else
                    lightDir = -targetCamera.transform.forward;
            }
        }

        // ---- input abstraction (mouse, touch or pen via Pointer; legacy fallback) ---
        // Pointer.current resolves to the mouse on desktop and the touchscreen on
        // mobile, so the same drag logic drives both.
        static Vector2 MousePos()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null ? Pointer.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
        static bool MouseDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }
        static bool MouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }
        static bool MouseUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(0);
#endif
        }

        // True while two or more fingers are down, so single-touch ripple/orbit
        // yields to the camera's pinch-zoom.
        static bool MultiTouch()
        {
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts == null) return false;
            int n = 0;
            foreach (var t in ts.touches)
                if (t.press.isPressed) n++;
            return n >= 2;
#else
            return Input.touchCount >= 2;
#endif
        }
        static bool KeySpaceDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }
        static bool KeyLHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.lKey.isPressed;
#else
            return Input.GetKey(KeyCode.L);
#endif
        }
    }
}
