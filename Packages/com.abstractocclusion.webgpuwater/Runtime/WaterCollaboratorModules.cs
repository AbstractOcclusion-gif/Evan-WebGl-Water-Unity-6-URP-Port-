// WebGL Water - Phase 1 lifecycle modules (Unity 6 / URP port).
//
// These are thin adapters that formalise the collaborators WaterVolume already owned and constructed
// by hand in TryInitialize. Each module owns its instance and maps the former inline construction /
// disposal onto the IWaterModule seam, so the master drives them through a registry.
//
// Reuse over rewrite: the collaborator classes themselves are untouched. Behaviour is byte-for-byte
// unchanged — the instances, their constructor arguments and the Enabled gates match the original
// TryInitialize exactly. The `context` parameter of Initialize is part of the shared contract; it
// becomes the source of per-frame state once Tick moves onto these modules in a later increment.
namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>GPU heightfield simulation (ping-pong RTs, compute dispatch): the always-on core.</summary>
    internal sealed class SimulationModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterSimulation Simulation { get; private set; }

        public SimulationModule(WaterVolume owner) => _owner = owner;

        public bool Enabled => true;

        public void Initialize(WaterContext context)
            => Simulation = new WaterSimulation(_owner.simCompute, _owner.SimResolution);

        public void Dispose()
        {
            Simulation?.Dispose();
            Simulation = null;
        }
    }

    /// <summary>Rasterized submerged-footprint pass; only when its shader is wired (FootprintDelta mode).</summary>
    internal sealed class ObstacleModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterObstacle Obstacle { get; private set; }

        public ObstacleModule(WaterVolume owner) => _owner = owner;

        public bool Enabled => _owner.obstacleShader != null;

        public void Initialize(WaterContext context)
            => Obstacle = new WaterObstacle(_owner.obstacleShader, _owner.SimResolution,
                                            _owner.VolumeCenter, _owner.VolumeRotation, _owner.VolumeExtentSafe);

        public void Dispose()
        {
            Obstacle?.Dispose();
            Obstacle = null;
        }
    }

    /// <summary>Per-body caustic material / RT / command buffer.</summary>
    internal sealed class CausticsModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterCausticsPass Caustics { get; private set; }

        public CausticsModule(WaterVolume owner) => _owner = owner;

        public bool Enabled => true;

        public void Initialize(WaterContext context)
            => Caustics = new WaterCausticsPass(_owner.causticsShader, _owner.largeBodyCausticsShader,
                                                _owner.causticResolution);

        public void Dispose()
        {
            Caustics?.Dispose();
            Caustics = null;
        }
    }

    /// <summary>Async height readback + CPU bilinear surface queries (buoyancy).</summary>
    internal sealed class SurfaceSamplerModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterSurfaceSampler Sampler { get; private set; }

        public SurfaceSamplerModule(WaterVolume owner) => _owner = owner;

        public bool Enabled => true;

        public void Initialize(WaterContext context) => Sampler = new WaterSurfaceSampler(_owner);

        // The original released the reference without an explicit Dispose; matched here exactly.
        public void Dispose() => Sampler = null;
    }

    /// <summary>Ocean-only FFT-cascade wave pass; null on pools / bounded bodies (analytic path kept).</summary>
    internal sealed class OceanFftModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterOceanFft OceanFft { get; private set; }

        public OceanFftModule(WaterVolume owner) => _owner = owner;

        // An unbounded-ocean body whose FFT compute is wired. Any other body skips it and keeps the
        // analytic large-wave path — byte-for-byte the original condition in TryInitialize.
        public bool Enabled => _owner.IsOceanClipmap && _owner.oceanFftCompute != null;

        public void Initialize(WaterContext context)
            => OceanFft = new WaterOceanFft(_owner.oceanFftCompute, WaterOceanFft.DefaultResolution,
                                            WaterOceanFft.DefaultCascadeCount, WaterOceanFft.DefaultDomainSizes);

        public void Dispose()
        {
            OceanFft?.Dispose();
            OceanFft = null;
        }
    }

    /// <summary>Shoreline shallow-water (SWE) sim zone (Layer C): emergent breaking + run-up near the
    /// waterline. Built only when a terrain + the SWE compute are wired; the actual solve is further
    /// gated inside the driver on Layer A's depth field being baked (so a runtime Use-Bed-Depth toggle
    /// lights it up without a re-enable). Any body without both stays byte-for-byte unchanged.</summary>
    internal sealed class ShoreSweModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterShoreSwe Swe { get; private set; }

        public ShoreSweModule(WaterVolume owner) => _owner = owner;

        // DISABLED (2026-07-13): Layer C is parked pending a Unity compile check of WaterShoreSwe.compute.
        // Never construct/dispatch the SWE zone, so a broken compute can't take down the body (and shoal)
        // in OnEnable. Re-enable with `_owner.sweCompute != null` once the compute is verified to compile.
        public bool Enabled => false;

        public void Initialize(WaterContext context)
            => Swe = new WaterShoreSwe(_owner.sweCompute, _owner.sweResolution, _owner, _owner.sweZoneMeters);

        public void Dispose()
        {
            Swe?.Dispose();
            Swe = null;
        }
    }

    /// <summary>Camera-following scrolling sim window for large bodies.</summary>
    internal sealed class SimWindowModule : IWaterModule
    {
        readonly WaterVolume _owner;
        public WaterSimWindow SimWindow { get; private set; }

        public SimWindowModule(WaterVolume owner) => _owner = owner;

        public bool Enabled => true;

        public void Initialize(WaterContext context) => SimWindow = new WaterSimWindow(_owner);

        // The original released the reference without an explicit Dispose; matched here exactly.
        public void Dispose() => SimWindow = null;
    }
}
