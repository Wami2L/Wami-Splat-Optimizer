// SplatKit — applies a quality profile to the splat renderers and keeps the frame rate (adaptive budget).
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Gsplat;
using UnityEngine;
#if SPLATKIT_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace SplatKit
{
    /// <summary>
    /// Aplica un perfil de calidad (Resources/SplatKitProfiles.json, el mismo que usa `splatkit preparar`) a los
    /// GsplatRenderer y SplatLod de sus hijos (o de toda la escena) y, si el perfil lo pide, ajusta el presupuesto de
    /// splats y la resolución con el tiempo real de GPU para mantener la frecuencia objetivo.
    ///
    /// Applies a quality profile to the child (or scene-wide) GsplatRenderers and SplatLods and, when the profile is
    /// adaptive, tunes the splat budget and the resolution from the measured GPU time to hold the target rate.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("Wami Splat Optimizer/Splat Quality")]
    public class SplatQuality : MonoBehaviour
    {
        [Tooltip("Id del perfil (auto, quest2, quest3, pcvr_low, pcvr_high, desktop, ultra) / profile id")]
        public string profile = "auto";
        [Tooltip("Aplicar a toda la escena en lugar de solo a los hijos / apply scene-wide instead of children only")]
        public bool sceneWide;
        [Tooltip("Usar los valores de 'custom' en lugar de los del perfil / use 'custom' instead of the profile values")]
        public bool useCustom;
        public SplatRuntimeSettings custom = new SplatRuntimeSettings();
        [Tooltip("Cambiar la escala de render de URP (también la de los ojos en XR) / drive the URP render scale")]
        public bool controlResolution = true;
        [Tooltip("Mostrar el perfil, los splats y el tiempo de GPU / show profile, splats and GPU time")]
        public bool overlay;

        /// <summary>Perfil en uso (tras resolver «auto»). / Profile in use (after resolving "auto").</summary>
        public SplatProfile Active { get; private set; }
        public SplatRuntimeSettings Settings { get; private set; }
        public float BudgetScale { get; private set; } = 1f;
        public float ResolutionScale { get; private set; } = 1f;
        public float GpuMs { get; private set; }

        readonly List<GsplatRenderer> m_renderers = new();
        readonly List<SplatLod> m_lods = new();
        readonly Dictionary<SplatLod, int> m_baseBudget = new();
        readonly FrameTiming[] m_timings = new FrameTiming[1];
        float m_nextAdapt, m_gpuAccum;
        int m_gpuSamples;
        float m_originalRenderScale = -1;

        public static bool XrActive
        {
            get
            {
#if SPLATKIT_XR
                return UnityEngine.XR.XRSettings.isDeviceActive;
#else
                return false;
#endif
            }
        }

        void OnEnable() => Apply();

        void OnDisable() => RestoreResolution();

        void OnValidate()
        {
            if (isActiveAndEnabled) Apply();
        }

        /// <summary>Cambia de perfil en caliente (sin recargar datos). / Switch profile at runtime (no reload).</summary>
        public void SetProfile(string id)
        {
            profile = id;
            Apply();
        }

        /// <summary>Resuelve el perfil y lo aplica a los renderers. / Resolve the profile and apply it.</summary>
        public void Apply()
        {
            var set = SplatProfileSet.Builtin;
            var p = set.Get(string.IsNullOrEmpty(profile) ? set.@default : profile) ?? set.Get("auto");
            if (p == null) return;
            if (p.auto) p = set.ForThisDevice(XrActive);
            Active = p;
            Settings = (useCustom ? custom : p.runtime).Clone();
            BudgetScale = 1f;
            ResolutionScale = 1f;
            Collect();
            m_baseBudget.Clear();
            var s = Settings;
            var xr = XrActive;
            foreach (var gs in m_renderers)
            {
                gs.SHDegree = s.sh_degree;
                gs.DepthPrepass = s.depth_prepass;
                gs.SortMode = GsplatRenderer.GsplatSortMode.OnCameraMove;
                gs.SortKey = s.sort_mode == "radial" ? GsplatRenderer.SortKeyMode.Radial : GsplatRenderer.SortKeyMode.View;
                gs.SortKeyBits = s.sort_key_bits;
                gs.ForceRefresh();
            }
            foreach (var lod in m_lods)
            {
                lod.angleDeg = s.lod_angle_deg;
                lod.budget = s.splat_budget;
                lod.stableXr = s.stable_xr_lod && (xr || Application.platform == RuntimePlatform.Android);
                lod.behindPenalty = s.behind_penalty;
                lod.periphery = s.periphery;
                m_baseBudget[lod] = s.splat_budget;
                lod.SetDirty();
            }
            GsplatQuality.MinPixelSize = s.min_pixel_size;
            GsplatQuality.MinContribution = s.min_contribution;
            ApplyResolution();
        }

        void Collect()
        {
            m_renderers.Clear();
            m_lods.Clear();
            if (sceneWide)
            {
                m_renderers.AddRange(FindObjectsByType<GsplatRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                m_lods.AddRange(FindObjectsByType<SplatLod>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            }
            else
            {
                m_renderers.AddRange(GetComponentsInChildren<GsplatRenderer>(true));
                m_lods.AddRange(GetComponentsInChildren<SplatLod>(true));
            }
        }

        void ApplyResolution()
        {
            if (!controlResolution || Settings == null || !Application.isPlaying) return;
#if SPLATKIT_URP
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (m_originalRenderScale < 0) m_originalRenderScale = urp.renderScale;
                urp.renderScale = Mathf.Clamp(Settings.splat_resolution * ResolutionScale, 0.1f, 2f);
            }
#endif
        }

        void RestoreResolution()
        {
#if SPLATKIT_URP
            if (m_originalRenderScale >= 0 && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
                urp.renderScale = m_originalRenderScale;
#endif
            m_originalRenderScale = -1;
        }

        void Update()
        {
            if (!Application.isPlaying || Settings == null) return;
            FrameTimingManager.CaptureFrameTimings();
            var gpu = FrameTimingManager.GetLatestTimings(1, m_timings) > 0 ? (float)m_timings[0].gpuFrameTime : 0f;
            if (gpu <= 0) gpu = Time.unscaledDeltaTime * 1000f;     // sin estadísticas de GPU: el tiempo de cuadro
            m_gpuAccum += gpu;
            m_gpuSamples++;
            if (!Settings.adaptive || Settings.target_hz <= 0 || Time.unscaledTime < m_nextAdapt) return;

            m_nextAdapt = Time.unscaledTime + 0.5f;
            GpuMs = m_gpuAccum / Mathf.Max(m_gpuSamples, 1);
            m_gpuAccum = 0;
            m_gpuSamples = 0;
            var target = 1000f / Settings.target_hz * 0.9f;            // 10 % de margen para el compositor del visor
            var budget = BudgetScale;
            var res = ResolutionScale;
            if (GpuMs > target)
            {
                if (budget > 0.35f) budget = Mathf.Max(0.35f, budget * 0.85f);
                else res = Mathf.Max(0.7f, res - 0.05f);
            }
            else if (GpuMs < target * 0.7f)
            {
                if (res < 1f) res = Mathf.Min(1f, res + 0.05f);
                else budget = Mathf.Min(1f, budget * 1.08f);
            }
            if (Mathf.Abs(budget - BudgetScale) > 0.02f)
            {
                BudgetScale = budget;
                foreach (var lod in m_lods)
                {
                    if (!lod || !m_baseBudget.TryGetValue(lod, out var b) || b <= 0) continue;
                    lod.budget = Mathf.RoundToInt(b * BudgetScale);
                    lod.SetDirty();
                }
            }
            if (Mathf.Abs(res - ResolutionScale) > 0.01f)
            {
                ResolutionScale = res;
                ApplyResolution();
            }
        }

        void OnGUI()
        {
            if (!overlay || Active == null) return;
            var splats = 0;
            foreach (var lod in m_lods)
                if (lod && lod.isActiveAndEnabled) splats += lod.SelectedSplats;
            var text = SplatLang.T(
                $"Wami Splat Optimizer · {Active.Label} · {splats:N0} splats · GPU {GpuMs:F1} ms · presupuesto ×{BudgetScale:F2} · resolución ×{Settings.splat_resolution * ResolutionScale:F2}",
                $"Wami Splat Optimizer · {Active.Label} · {splats:N0} splats · GPU {GpuMs:F1} ms · budget ×{BudgetScale:F2} · resolution ×{Settings.splat_resolution * ResolutionScale:F2}");
            GUI.Box(new Rect(10, Screen.height - 34, 820, 24), text);
        }
    }
}
