// WebGpuWater - per-body caustics render pass.
// Extracted from WaterVolume: owns the caustic material, render target and command
// buffer, and renders the body's own sim into its own caustic RT - so caustics never
// come from whatever body last wrote the _WaterTex global. The RT reaches the body's
// renderers via the property block; the primary also mirrors it to the _CausticTex
// global for objects without a WaterMembership.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterCausticsPass
    {
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");

        readonly Material _material;
        readonly RenderTexture _target;
        readonly CommandBuffer _cb;

        internal RenderTexture Texture => _target;

        internal WaterCausticsPass(Shader causticsShader, int resolution)
        {
            if (causticsShader == null) throw new System.ArgumentNullException(nameof(causticsShader));
            if (resolution <= 0)
                throw new System.ArgumentException($"Caustic resolution must be positive, got {resolution}.",
                                                   nameof(resolution));

            // HideAndDontSave: an edit-mode preview must never serialize these into the scene.
            _material = new Material(causticsShader) { hideFlags = HideFlags.HideAndDontSave };
            _target = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex",
                hideFlags = HideFlags.HideAndDontSave
            };
            _target.Create();
            _cb = new CommandBuffer { name = "WebGLWater.Caustics" };
        }

        // Project the body's own sim state into its caustic RT (vertex shader outputs
        // clip space directly, so the mesh draws with an identity matrix).
        internal void Render(Mesh waterMesh, RenderTexture simTexture)
        {
            if (simTexture != null) _material.SetTexture(ID_Water, simTexture);

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _material, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        internal void Dispose()
        {
            _cb?.Release();
            // Release frees the GPU surface immediately; Destroy frees the wrapper objects,
            // which otherwise accumulate across enable/disable cycles until scene unload.
            if (_target != null)
            {
                _target.Release();
                DestroyRuntimeObject(_target);
            }
            DestroyRuntimeObject(_material);
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
