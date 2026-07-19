// WebGpuWater - shared serialized-property paths into WaterVolume.
//
// WHY: these paths are raw strings with zero compile-time safety (a stale path already caused a
// crash once - see WaterVolumeEditor.Setup's history note), and the same paths were retyped in
// the wizard, the inspector's body-type defaults and the ocean section. One registry means a
// field rename is a one-line fix and every consumer breaks loudly together in review, not
// silently apart at runtime.
namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterVolumePropertyPaths
    {
        internal const string OpenWater = "ocean.openWater";
        internal const string UnboundedOcean = "ocean.unboundedOcean";
        internal const string ScreenSpaceReflection = "reflectionSettings.useScreenSpaceReflection";
        internal const string PlanarReflection = "reflectionSettings.usePlanarReflection";
        internal const string BodyType = "bodyType";
        internal const string EnableLargeBodyWindow = "enableLargeBodyWindow";
    }
}
