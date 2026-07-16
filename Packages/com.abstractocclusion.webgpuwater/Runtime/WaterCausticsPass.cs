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
        static readonly int ID_SimCenter = Shader.PropertyToID("_SimCenter");
        static readonly int ID_SimExtent = Shader.PropertyToID("_SimExtent");
        static readonly int ID_LightDir = Shader.PropertyToID("_LightDir");
        static readonly int ID_VolumeCenter = Shader.PropertyToID("_VolumeCenter");
        static readonly int ID_VolumeExtent = Shader.PropertyToID("_VolumeExtent");
        static readonly int ID_VolumeRot = Shader.PropertyToID("_VolumeRot");
        static readonly int ID_OccluderActive = Shader.PropertyToID("_CausticOccluderActive");

        // Green channel of the caustic RT starts at 1 (unshadowed) so floor fragments that sample
        // outside the drawn caustic footprint read "lit", not black, now that green drives the
        // underwater object shadow. The occluder pass writes 0 under a submerged object.
        static readonly Color CausticClear = new Color(0f, 1f, 0f, 0f);

        readonly Material _material;
        readonly Material _largeBodyMaterial; // null when the large-body caustics shader isn't assigned (oceans only)
        readonly Material _occluderMaterial;  // null when the occluder shader isn't assigned -> object shadows stay on the shadow map
        readonly RenderTexture _target;
        readonly CommandBuffer _cb;

        internal RenderTexture Texture => _target;

        internal WaterCausticsPass(Shader causticsShader, Shader largeBodyCausticsShader,
                                   Shader occluderShader, int resolution)
        {
            if (causticsShader == null) throw new System.ArgumentNullException(nameof(causticsShader));
            if (resolution <= 0)
                throw new System.ArgumentException($"Caustic resolution must be positive, got {resolution}.",
                                                   nameof(resolution));

            // HideAndDontSave: an edit-mode preview must never serialize these into the scene.
            _material = new Material(causticsShader) { hideFlags = HideFlags.HideAndDontSave };
            // Optional: only the windowed ocean uses it, so a project without the shader assigned simply
            // gets no large-body caustics (the shafts still read as plain shadow shafts).
            if (largeBodyCausticsShader != null)
                _largeBodyMaterial = new Material(largeBodyCausticsShader) { hideFlags = HideFlags.HideAndDontSave };
            // Optional: submerged objects project their silhouette along the refracted light into the
            // caustic RT green channel, so their underwater shadow lines up with the caustics.
            if (occluderShader != null)
                _occluderMaterial = new Material(occluderShader) { hideFlags = HideFlags.HideAndDontSave };
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
        internal void Render(Mesh waterMesh, RenderTexture simTexture, float waterRestY,
                             Vector3 volumeCenter, Vector3 volumeExtent, Quaternion volumeRotation,
                             Vector3 lightDir)
        {
            if (simTexture != null) _material.SetTexture(ID_Water, simTexture);

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, CausticClear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _material, 0, 0);
            DrawOccluders(waterRestY, volumeCenter, volumeExtent, volumeRotation, lightDir);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        // Project every submerged interactable along the refracted light into the caustic RT green
        // channel (0 = occluded), using the same ProjectCausticUV mapping the floor samples with - so
        // the object shadow is registered with the caustics, not the un-refracted shadow map. The volume
        // frame is set on the material explicitly because the body publishes those globals only after
        // this pass runs. _CausticOccluderActive tells the pool/receiver shaders to source the underwater
        // object shadow from green (0 -> unchanged legacy shadow-map look).
        void DrawOccluders(float waterRestY, Vector3 volumeCenter, Vector3 volumeExtent,
                           Quaternion volumeRotation, Vector3 lightDir)
        {
            if (_occluderMaterial == null) { Shader.SetGlobalFloat(ID_OccluderActive, 0f); return; }

            _occluderMaterial.SetVector(ID_LightDir, lightDir);
            _occluderMaterial.SetVector(ID_VolumeCenter, volumeCenter);
            _occluderMaterial.SetVector(ID_VolumeExtent, volumeExtent);
            _occluderMaterial.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(volumeRotation));

            var list = WaterInteractable.Active;
            bool drewAny = false;
            for (int i = 0; i < list.Count; i++)
            {
                WaterInteractable it = list[i];
                if (it == null || it.Renderer == null) continue;
                if (!it.IsSubmerged(it.WaterlineY(waterRestY))) continue;
                _cb.DrawRenderer(it.Renderer, _occluderMaterial, 0, 0);
                drewAny = true;
            }

            Shader.SetGlobalFloat(ID_OccluderActive, drewAny ? 1f : 0f);
        }

        // Ocean version: project the near-field WINDOW sim into the caustic RT via the large-body
        // (world-frame) caustic. The window centre/extent are set on the material explicitly so the
        // projection frame is correct even on the first frame, before the body publishes those globals.
        // No-op when the large-body shader isn't assigned, so oceans just fall back to plain shafts.
        internal void RenderLargeBody(Mesh windowMesh, RenderTexture simTexture,
                                      Vector3 windowCenter, Vector3 windowHalfExtent)
        {
            if (_largeBodyMaterial == null || windowMesh == null) return;
            if (simTexture != null) _largeBodyMaterial.SetTexture(ID_Water, simTexture);
            _largeBodyMaterial.SetVector(ID_SimCenter, windowCenter);
            _largeBodyMaterial.SetVector(ID_SimExtent, windowHalfExtent);

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(windowMesh, Matrix4x4.identity, _largeBodyMaterial, 0, 0);
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
            DestroyRuntimeObject(_largeBodyMaterial);
            DestroyRuntimeObject(_occluderMaterial);
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
