// WebGL Water - the single authoring entry point.
//
// Menu: AbstractOcclusion > WebGpuWater > Water Wizard
//
// One window that builds a configured water surface (size, analytic pool, god rays, foam
// particles, surface foam + conditional edge foam) and optionally turns scene objects into
// floatable or interactable props. The retrofit one-off operations live below as buttons, so
// the whole toolset is reachable from a single menu entry. Scene composition is delegated to
// WaterBuildKit; this file only maps UI state onto those generators.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed class WaterWizardWindow : EditorWindow
    {
        const string MenuPath = "AbstractOcclusion/WebGpuWater/Water Wizard";
        const string WindowTitle = "Water Wizard";

        const string RootObjectName = "WebGL Water";
        const string WaterBodyName = "Water Body";

        static readonly Vector3 DefaultExtent = new Vector3(2f, 1f, 2f);
        const float MinExtentComponent = 0.05f;

        // WaterVolume.foamBorderWidth default; applied when edge foam is enabled, zeroed otherwise.
        const float EdgeFoamBorderWidth = 0.08f;

        // Floor collider sizing, expressed relative to the water extent so props always land inside.
        const float FloorThickness = 0.1f;
        const float FloorDropBelowFloorMargin = 0.05f;
        const float FloorHorizontalScale = 2f;

        enum InteractionMode { Floatable, InteractableStatic }

        Vector3 _extent = DefaultExtent;
        bool _analyticPool = true;
        bool _godRays = true;
        bool _useFoamParticles = true;
        bool _surfaceFoam;
        bool _edgeFoam;
        bool _addFloorCollider = true;
        InteractionMode _objectMode = InteractionMode.Floatable;
        readonly List<GameObject> _objects = new List<GameObject>();
        Vector2 _scroll;

        [MenuItem(MenuPath)]
        static void Open()
        {
            var window = GetWindow<WaterWizardWindow>(utility: false, title: WindowTitle, focus: true);
            window.minSize = new Vector2(340f, 420f);
            window.Show();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawCreateSection();
            EditorGUILayout.Space(12f);
            DrawUtilitiesSection();
            EditorGUILayout.EndScrollView();
        }

        // ---- create section --------------------------------------------------
        void DrawCreateSection()
        {
            EditorGUILayout.LabelField("Create Water Surface", EditorStyles.boldLabel);

            _extent = EditorGUILayout.Vector3Field(
                new GUIContent("Size (extent)", "Half-extents of the water volume: X/Z horizontal, Y depth."),
                _extent);

            _analyticPool = EditorGUILayout.Toggle(
                new GUIContent("Analytic pool", "Add the shader-drawn pool walls/floor with caustics."), _analyticPool);
            _godRays = EditorGUILayout.Toggle(
                new GUIContent("God rays", "Underwater caustic-masked light shafts."), _godRays);
            _useFoamParticles = EditorGUILayout.Toggle(
                new GUIContent("Foam particles", "Attach the GPU foam/spray particle system (idles until surface foam is on)."),
                _useFoamParticles);

            _surfaceFoam = EditorGUILayout.Toggle(
                new GUIContent("Surface foam", "Turbulence-driven foam shaded on the water surface."), _surfaceFoam);

            using (new EditorGUI.DisabledScope(!_surfaceFoam))
            {
                EditorGUI.indentLevel++;
                _edgeFoam = EditorGUILayout.Toggle(
                    new GUIContent("Edge foam", "Foam band along the pool walls (only with surface foam)."),
                    _surfaceFoam && _edgeFoam);
                EditorGUI.indentLevel--;
            }

            _addFloorCollider = EditorGUILayout.Toggle(
                new GUIContent("Floor collider", "Thin collider under the water so sinking props have something to rest on."),
                _addFloorCollider);

            EditorGUILayout.Space(6f);
            DrawObjectsList();

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(!ExtentIsValid()))
            {
                if (GUILayout.Button("Create Water Surface", GUILayout.Height(30f)))
                    CreateWaterSurface();
            }
            if (!ExtentIsValid())
                EditorGUILayout.HelpBox($"Every size component must be at least {MinExtentComponent}.", MessageType.Warning);
        }

        void DrawObjectsList()
        {
            EditorGUILayout.LabelField("Objects to add to the water", EditorStyles.miniBoldLabel);
            _objectMode = (InteractionMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode", "Floatable = buoyant rigidbody prop; Interactable = static object that displaces the surface."),
                _objectMode);

            for (int i = 0; i < _objects.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _objects[i] = (GameObject)EditorGUILayout.ObjectField(_objects[i], typeof(GameObject), allowSceneObjects: true);
                if (GUILayout.Button("-", GUILayout.Width(24f)))
                {
                    _objects.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Add object slot"))
                _objects.Add(null);
        }

        bool ExtentIsValid()
        {
            return _extent.x >= MinExtentComponent
                && _extent.y >= MinExtentComponent
                && _extent.z >= MinExtentComponent;
        }

        void CreateWaterSurface()
        {
            if (!ExtentIsValid())
            {
                Debug.LogError("[WebGL Water] Water not created: every size component must be positive.");
                return;
            }

            var root = new GameObject(RootObjectName);
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildPoolMaterial: _analyticPool))
            {
                Object.DestroyImmediate(root);
                return;
            }

            var body = CreateWaterBody(ctx, root.transform, WaterBodyName, Vector3.zero, _extent,
                                       primary: true, withPool: _analyticPool, withGodRays: _godRays,
                                       withFoamParticles: _useFoamParticles);
            ApplyFoamSettings(body);

            if (_addFloorCollider)
                CreateFloorForExtent(root.transform, _extent);

            WireObjects(body);

            Selection.activeObject = root;
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGL Water] Water surface built ({RootObjectName}). Press Play.");
        }

        void ApplyFoamSettings(WaterVolume body)
        {
            body.foam = _surfaceFoam;
            body.foamBorderWidth = (_surfaceFoam && _edgeFoam) ? EdgeFoamBorderWidth : 0f;
            EditorUtility.SetDirty(body);
        }

        void CreateFloorForExtent(Transform parent, Vector3 extent)
        {
            var center = new Vector3(0f, -(extent.y + FloorDropBelowFloorMargin), 0f);
            var size = new Vector3(extent.x * FloorHorizontalScale, FloorThickness, extent.z * FloorHorizontalScale);
            CreateFloorCollider(parent, center, size);
        }

        void WireObjects(WaterVolume body)
        {
            foreach (GameObject go in _objects)
            {
                if (go == null) continue;
                if (_objectMode == InteractionMode.Floatable) MakeFloatable(go);
                else MakeInteractable(go);
                EditorUtility.SetDirty(go);
            }
        }

        // A buoyant prop mirrors WaterBuildKit.CreateFloatingProp's component set, applied in place
        // so the user's own mesh/material/transform are preserved.
        static void MakeFloatable(GameObject go)
        {
            EnsureComponent<Rigidbody>(go);
            EnsureComponent<WaterInteractable>(go);
            EnsureComponent<WaterBuoyancy>(go);
            EnsureComponent<WaterSplash>(go);
            EnsureComponent<WaterMembership>(go);
        }

        // A static interactable displaces the surface but stays put (no Rigidbody), mirroring
        // WaterBuildKit.CreateStaticObstacle.
        static void MakeInteractable(GameObject go)
        {
            EnsureComponent<WaterInteractable>(go);
            EnsureComponent<WaterMembership>(go);
        }

        static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        // ---- utilities section ----------------------------------------------
        void DrawUtilitiesSection()
        {
            EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);

            if (GUILayout.Button(new GUIContent("Create WaterVolume Prefab",
                "Save a reusable single-body water prefab that resolves camera/sun at runtime.")))
                WaterSceneBuilder.CreateWaterVolumePrefab();

            if (GUILayout.Button(new GUIContent("Add Foam Particles To Selected",
                "Retrofit GPU foam particles onto the selected WaterVolume.")))
                WaterSceneBuilder.AddFoamParticlesToSelection();

            if (GUILayout.Button(new GUIContent("Assign Foam Textures To Scene Water",
                "Assign the foam flipbook + normal map to every water material in the open scene.")))
                WaterSceneBuilder.AssignFoamTexturesToSceneWater();

            if (GUILayout.Button(new GUIContent("Upgrade Splash Materials (lit)",
                "Upgrade the shared splash materials to the lit splash shader in place.")))
                WaterSceneBuilder.UpgradeSplashMaterialsMenu();

            if (GUILayout.Button(new GUIContent("Add Water Body (secondary)",
                "Add a second, independent water body next to the primary one.")))
                WaterSceneBuilder.AddSecondaryBody();
        }
    }
}
