// WebGL Water - one-shot demo reorganization utility.
//
// TEMPORARY: this file exists only to migrate the project once. Delete it after the
// menu command has run successfully.
//
// Menu: Tools > WebGL Water > Maintenance > Reorganize Demos (one-shot)
//
// It consolidates the 8 authored demo scenes and their materials into a standard package
// demo layout (Assets/WebGLWater/Demos/{Scenes,Materials}) and removes the superseded old
// demo system (Assets/Demos) plus the orphan pre-migration materials at the Generated root.
// Every move uses AssetDatabase so GUID references inside the scenes are preserved.
using UnityEditor;
using UnityEngine;

namespace WebGLWater.EditorTools
{
    internal static class DemoReorganizer
    {
        const string MenuPath = "Tools/WebGL Water/Maintenance/Reorganize Demos (one-shot)";

        const string PackageRoot = "Assets/WebGLWater";
        const string GeneratedRoot = PackageRoot + "/Generated";
        const string GeneratedDemos = GeneratedRoot + "/Demos";
        const string DemosRoot = PackageRoot + "/Demos";
        const string DemosScenes = DemosRoot + "/Scenes";
        const string DemosMaterials = DemosRoot + "/Materials";

        const string LegacyRoot = "Assets/Demos";
        const string LegacyScenes = LegacyRoot + "/Scenes";

        const string SceneExtension = ".unity";
        const string MaterialExtension = ".mat";
        const string LogPrefix = "[WebGL Water] ";

        // Existing scene file name (no extension) -> renamed demo scene name (no extension).
        // The mapping was verified against each scene's referenced Generated/Demos material folder.
        static readonly (string source, string renamed)[] SceneRenames =
        {
            ("ClassicPool",            "1. Classic Pool"),
            ("Seep Pool",              "2. Deep Lake"),
            ("Lake",                   "3. Terrain Lake"),
            ("Multiple Water Volumes", "4. Multi-Lake"),
            ("Underwater",             "5. Underwater"),
            ("OpenWater",              "6. Open Water"),
            ("Reflections",            "7. Reflections Trio"),
            ("custom pool",            "8. Custom Object Pool"),
        };

        // Per-demo material subfolders to relocate out of Generated/Demos into Demos/Materials.
        static readonly string[] DemoMaterialFolders =
        {
            "ClassicPool", "DeepLake", "TerrainLake", "MultiLake",
            "Underwater", "OpenWater", "ReflectionsTrio", "CustomObjectPool",
        };

        // Orphan root-level Generated materials, verified unreferenced by the 8 demos.
        // Sphere.mat / UnitSphere.asset are deliberately excluded - they belong to audit item #21.
        static readonly string[] OrphanRootMaterials =
        {
            "Crate", "DeepCrateA", "DeepCrateB", "DeepPillar",
            "GodRays", "Pool", "WaterAbove", "WaterUnder",
        };

        [MenuItem(MenuPath)]
        static void Run()
        {
            if (!ConfirmWithUser()) return;

            AssetDatabase.SaveAssets();
            EnsureTargetFolders();
            MoveAndRenameScenes();
            MoveDemoMaterialFolders();
            DeleteOrphanRootMaterials();
            DeleteEmptyGeneratedDemos();
            DeleteLegacyDemoSystem();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(LogPrefix + "Demo reorganization complete. Open each scene to verify, then delete DemoReorganizer.cs.");
        }

        static bool ConfirmWithUser()
        {
            return EditorUtility.DisplayDialog(
                "Reorganize Demos",
                "This will:\n" +
                "  - move + rename the 8 demo scenes into " + DemosScenes + "\n" +
                "  - move their materials into " + DemosMaterials + "\n" +
                "  - delete the orphan root materials in " + GeneratedRoot + "\n" +
                "  - delete the entire old demo system (" + LegacyRoot + ")\n\n" +
                "This is not easily undoable. Proceed?",
                "Reorganize", "Cancel");
        }

        static void EnsureTargetFolders()
        {
            CreateFolderIfMissing(PackageRoot, "Demos");
            CreateFolderIfMissing(DemosRoot, "Scenes");
            CreateFolderIfMissing(DemosRoot, "Materials");
        }

        static void MoveAndRenameScenes()
        {
            foreach (var (source, renamed) in SceneRenames)
            {
                string from = LegacyScenes + "/" + source + SceneExtension;
                string to = DemosScenes + "/" + renamed + SceneExtension;
                MoveAssetOrThrow(from, to);
            }
        }

        static void MoveDemoMaterialFolders()
        {
            foreach (string folder in DemoMaterialFolders)
            {
                string from = GeneratedDemos + "/" + folder;
                string to = DemosMaterials + "/" + folder;
                MoveAssetOrThrow(from, to);
            }
        }

        static void DeleteOrphanRootMaterials()
        {
            foreach (string material in OrphanRootMaterials)
            {
                string path = GeneratedRoot + "/" + material + MaterialExtension;
                DeleteAssetIfPresent(path);
            }
        }

        static void DeleteEmptyGeneratedDemos()
        {
            if (!AssetDatabase.IsValidFolder(GeneratedDemos)) return;

            string[] remaining = AssetDatabase.FindAssets(string.Empty, new[] { GeneratedDemos });
            if (remaining.Length > 0)
            {
                Debug.LogWarning(LogPrefix + GeneratedDemos + " is not empty after the move; leaving it in place for manual review.");
                return;
            }
            DeleteAssetIfPresent(GeneratedDemos);
        }

        static void DeleteLegacyDemoSystem()
        {
            DeleteAssetIfPresent(LegacyRoot);
        }

        // ---- primitives with fail-fast validation -------------------------------

        static void CreateFolderIfMissing(string parent, string leaf)
        {
            string full = parent + "/" + leaf;
            if (AssetDatabase.IsValidFolder(full)) return;

            if (!AssetDatabase.IsValidFolder(parent))
                throw new System.InvalidOperationException(LogPrefix + "Parent folder does not exist: " + parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }

        static void MoveAssetOrThrow(string from, string to)
        {
            if (!AssetExists(from))
                throw new System.InvalidOperationException(LogPrefix + "Source asset not found: " + from);

            if (AssetExists(to))
                throw new System.InvalidOperationException(LogPrefix + "Destination already exists: " + to);

            string error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error))
                throw new System.InvalidOperationException(LogPrefix + "Move failed (" + from + " -> " + to + "): " + error);
        }

        static void DeleteAssetIfPresent(string path)
        {
            if (!AssetExists(path)) return;

            if (!AssetDatabase.DeleteAsset(path))
                throw new System.InvalidOperationException(LogPrefix + "Delete failed: " + path);
        }

        static bool AssetExists(string path)
        {
            return AssetDatabase.IsValidFolder(path) || AssetDatabase.LoadMainAssetAtPath(path) != null;
        }
    }
}
