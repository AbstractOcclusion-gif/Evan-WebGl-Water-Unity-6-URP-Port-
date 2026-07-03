// WebGpuWater - camera-following sim window for large bodies.
// Extracted from WaterVolume: owns the scrolling window's texel-snapped centre and
// tracks the camera - project it onto the surface plane, clamp into the footprint,
// snap to the sim-texel lattice, and scroll the sim state by the integer texel delta
// so ripples stay pinned in world space.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSimWindow
    {
        readonly WaterVolume _body;

        int _cellX, _cellZ; // window centre as integer texel indices in the volume's local frame
        bool _centerInit;

        /// <summary>World centre of the window, on the surface plane, texel-snapped.
        /// Defaults to the volume centre until the first Track().</summary>
        internal Vector3 Center { get; private set; }

        internal WaterSimWindow(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
            Center = body.VolumeCenter;
        }

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window). Shares the
        // volume rotation; centred on the scrolling window centre.
        internal Vector3 WorldToSim(Vector3 world)
        {
            Vector3 e = _body.SimHalfExtent;
            Vector3 local = Quaternion.Inverse(_body.VolumeRotation) * (world - Center);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // Move the window to the camera. Called once per simulated frame (and once at init
        // to prime the centre before the first publish).
        internal void Track()
        {
            Camera cam = _body.targetCamera;
            WaterSimulation sim = _body.Simulation;
            if (cam == null || sim == null) return;

            // Camera projected onto the surface plane (through the volume centre, along up).
            Vector3 up = _body.VolumeUp;
            Vector3 camPos = cam.transform.position;
            Vector3 onPlane = camPos - Vector3.Dot(camPos - _body.VolumeCenter, up) * up;

            // Work in the volume's local horizontal frame so the lattice is axis-aligned.
            Vector3 local = Quaternion.Inverse(_body.VolumeRotation) * (onPlane - _body.VolumeCenter);

            float texel = 2f * _body.simWindowMeters / _body.SimResolution;
            // Clamp the window centre so it stays inside the footprint (or may overhang the edge).
            Vector3 e = _body.VolumeExtentSafe;
            float limitX = _body.clampWindowToShore ? Mathf.Max(0f, e.x - _body.simWindowMeters) : e.x;
            float limitZ = _body.clampWindowToShore ? Mathf.Max(0f, e.z - _body.simWindowMeters) : e.z;
            float clampedX = Mathf.Clamp(local.x, -limitX, limitX);
            float clampedZ = Mathf.Clamp(local.z, -limitZ, limitZ);

            int cellX = Mathf.RoundToInt(clampedX / texel);
            int cellZ = Mathf.RoundToInt(clampedZ / texel);

            if (!_centerInit)
            {
                _cellX = cellX; _cellZ = cellZ;
                _centerInit = true;
            }
            else
            {
                int dx = cellX - _cellX;
                int dz = cellZ - _cellZ;
                if (dx != 0 || dz != 0)
                {
                    // Local x -> sim texel u, local z -> sim texel v. The kernel does
                    // Dst[p] = Src[p - offset]; offsetting by -delta keeps world features fixed
                    // (see WaterSimulation.Scroll).
                    sim.Scroll(-dx, -dz);
                    _cellX = cellX; _cellZ = cellZ;
                }
            }

            Center = _body.VolumeCenter + _body.VolumeRotation * new Vector3(_cellX * texel, 0f, _cellZ * texel);
        }
    }
}
