// WebGL Water - planar reflection binding (Unity 6 / URP port).
//
// Reflection mode + look is UNIFORM-driven, published per body every frame by WaterUniformPublisher,
// so there are no keywords to set and no per-body material instancing for reflection. The one piece
// that still needs a hook is the scene's single planar-reflection mirror: a planar body points it at
// its own plane and turns it on. Kept here so all reflection policy has one home.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Reflection policy for <see cref="WaterVolume"/>: bind the scene's planar mirror.</summary>
    internal static class WaterReflections
    {
        /// <summary>
        /// Point the scene's planar reflection at THIS body's plane and turn it on. The planar
        /// texture is a single global plane, so only one hero body should use planar reflection; with
        /// several, the last to enable at OnEnable wins.
        /// </summary>
        internal static void BindHeroPlanar(Camera targetCamera, float waterHeight)
        {
            if (targetCamera == null) return;
            var planar = targetCamera.GetComponent<PlanarReflection>();
            if (planar == null) return;
            planar.enableReflection = true;
            planar.waterHeight = waterHeight;
        }
    }
}
