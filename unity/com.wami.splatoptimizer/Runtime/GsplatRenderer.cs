// Copyright (c) 2025 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    [ExecuteAlways]
    [AddComponentMenu("Wami Splat Optimizer/Splat Renderer")]
    public class GsplatRenderer : MonoBehaviour, IGsplat
    {
        public enum GsplatSortMode
        {
            Always,
            SortEveryNFrames,
            CutoutsEveryNSorts,
            // SplatKit: sort only when a camera moved past SortMoveThreshold / SortRotateThresholdDeg (radial keys
            // ignore rotation) or the selection changed, like PlayCanvas. Default for new renderers.
            OnCameraMove,
        }

        /// <summary>SplatKit: what the sort key measures.</summary>
        public enum SortKeyMode
        {
            View,       // distance along the view axis (original gsplat)
            Radial,     // distance to the camera: head rotation does not change the order (VR)
        }

        public GsplatAsset GsplatAsset;

        // Range is enforced by GsplatRendererEditor based on the bound asset's SHBands.
        public int SHDegree = 3;
        [HideInInspector] public uint RenderOrder = 0;
        public float Brightness = 1.0f;

        [Tooltip(
            "Improves rendering speed by shrinking Gaussian splats while trying to keep the impact on visual quality as small as possible.")]
        [Range(0, 1)]
        public float SplatDownscaleFactor = 0.0f;

        public bool GammaToLinear;

        [Tooltip("SplatKit: the scene was trained with antialiasing (Mip-Splatting, LichtFeld --enable-mip): compensate " +
                 "the opacity of the 2D filter. / La escena se entrenó con antialias.")]
        public bool AntiAlias;

        [Tooltip("Pre-pasada de profundidad: los núcleos casi opacos ocultan por early-Z lo que tienen detrás " +
                 "(menos overdraw en interiores). Desactivada = comportamiento original.")]
        public bool DepthPrepass;
        [Range(0.3f, 1f)] public float DepthPrepassAlpha = 0.5f;
        [Tooltip("Margen relativo a la distancia: lo que esté hasta este % detrás del núcleo se sigue dibujando")]
        [Range(0f, 0.2f)] public float DepthPrepassBiasRel = 0.02f;
        [Tooltip("Margen absoluto (m) añadido al relativo")]
        [Range(0f, 0.5f)] public float DepthPrepassBiasAbs = 0.02f;
        public bool AsyncUpload;
        public bool RenderBeforeUploadComplete = true;

        [Tooltip("Does cutouts update the Gsplat world bounds? (Costly on moving cutouts)")]
        public bool CutoutsUpdateBounds = true;

        GsplatAsset m_prevAsset;
        GsplatRendererImpl m_renderer;

        public bool Valid => GsplatAsset &&
                             (RenderBeforeUploadComplete ? SplatCount > 0 : SplatCount == GsplatAsset.SplatCount);

        public uint SplatCount => m_renderer != null ? m_renderer.GsplatResource?.UploadedCount ?? 0 : 0;

        public ISorterResource SorterResource => m_renderer.SorterResource;

        // IGsplat global-merge members: expose per-renderer GPU buffers for the global sorter.
        public GsplatResource GsplatResource => m_renderer?.GsplatResource;
        public byte SHBands => GsplatAsset?.SHBands ?? 0;


        public uint RemainingCount
        {
            get => m_renderer.m_remainingCount;
            set => m_renderer.m_remainingCount = value;
        }

        public Bounds Bounds
        {
            get => m_renderer.m_bounds;
            set => m_renderer.m_bounds = value;
        }

        public GsplatCutout[] Cutouts
        {
            get
            {
                var cutouts = GsplatCutout.m_RegisteredCutouts
                    .Where(component => component.enabled)
                    .Where(component =>
                        component.m_Target == GsplatCutout.Target.All ||
                        (component.m_Target == GsplatCutout.Target.Parent && component.transform.parent == transform) ||
                        (component.m_Target == GsplatCutout.Target.Specific && component.m_SpecifcRenderer == this)
                    );
                return cutouts.ToArray();
            }
        }

        public bool ComputeSortRequired => m_renderer.ComputeSortRequired;
        public bool ComputeCutoutsRequired => m_renderer.ComputeCutoutsRequired;
        public GsplatSortMode SortMode = GsplatSortMode.OnCameraMove;
        [HideInInspector] public uint SortRefreshRate = 1;
        [HideInInspector] public uint CutoutsRefreshRate = 1;

        [Tooltip("SplatKit: Radial = VR (no re-sort when only rotating). / Radial para VR.")]
        public SortKeyMode SortKey = SortKeyMode.View;
        [Tooltip("SplatKit: 16 = quantized keys, half the sort work; 32 = original float keys.")]
        public int SortKeyBits = 16;
        [Tooltip("SplatKit (OnCameraMove): re-sort after the camera moves this far (m).")]
        public float SortMoveThreshold = 0.01f;
        [Tooltip("SplatKit (OnCameraMove, View keys): re-sort after the camera turns this much (degrees).")]
        public float SortRotateThresholdDeg = 1f;

        public bool NeedsSort(Camera camera) => SortMode == GsplatSortMode.OnCameraMove
            ? m_renderer.NeedsSortFor(camera, SortKey == SortKeyMode.Radial, SortMoveThreshold, SortRotateThresholdDeg)
            : m_renderer.ComputeSortRequired;

        SortKeyMode IGsplat.SortKey => SortKey;
        int IGsplat.SortKeyBits => SortKeyBits;

        public void ComputeDepth(CommandBuffer cmd, Matrix4x4 matrixMv) => m_renderer.ComputeDepth(cmd, matrixMv);

        void OnEnable()
        {
            GsplatSorter.Instance.RegisterGsplat(this);
            m_prevAsset = null;
        }

        void OnDisable()
        {
            GsplatSorter.Instance.UnregisterGsplat(this);
            m_renderer?.Dispose();
            m_renderer = null;
        }

        public void ForceRefresh()
        {
            m_renderer?.ForceRefresh();
        }

        /// <summary>
        /// Dibuja solo los splats indicados (los primeros <paramref name="count"/> índices del asset). Para LOD
        /// dinámico: un único asset con todos los niveles de todos los trozos y, cuando cambia la selección, la lista
        /// de los elegidos; ordenación y dibujo trabajan solo sobre ellos. Devuelve false si el asset aún no está
        /// subido a la GPU.
        /// </summary>
        public bool SetSelection(uint[] indices, int count)
        {
            if (m_renderer == null || m_renderer.GsplatResource == null) return false;
            m_renderer.SetSelection(indices, count);
            return true;
        }

        /// <summary>Vuelve a dibujar todos los splats del asset.</summary>
        public void ClearSelection() => m_renderer?.ClearSelection();

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            if (GsplatSettings.Instance.DisplayBoundingBoxes && Valid && isActiveAndEnabled)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(Bounds.center, Bounds.size);
            }
        }

        [SerializeField, HideInInspector] string m_assetGuid;
        public string AssetGuid => m_assetGuid;
#endif // #if UNITY_EDITOR

        void OnValidate()
        {
            ForceRefresh();
#if UNITY_EDITOR
            if (GsplatAsset &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(GsplatAsset, out var guid, out long localId))
                m_assetGuid = guid;
#endif // #if UNITY_EDITOR
        }

        public void ReloadAsset()
        {
            m_prevAsset = null;
        }

        /// <summary>
        /// SplatKit: true = Update() does nothing and the owner calls Tick() (benchmarks, custom loops), so the
        /// splats are queued exactly once per frame.
        /// </summary>
        [NonSerialized] public bool ExternalTick;

        public void Update()
        {
            if (!ExternalTick) Tick();
        }

        /// <summary>Upload, sort scheduling and draw submission for this frame (what Update does).</summary>
        public void Tick()
        {
            if (!GsplatAsset)
                m_prevAsset = null;
            if (m_prevAsset != GsplatAsset)
            {
                m_renderer?.ReleaseGsplatAsset();
                m_prevAsset = GsplatAsset;
                if (GsplatAsset)
                {
                    if (m_renderer == null)
                        m_renderer = new GsplatRendererImpl(GsplatAsset.SplatCount);
                    else
                        m_renderer.RecreateResources(GsplatAsset.SplatCount);
#if UNITY_EDITOR
                    var asyncUpload = AsyncUpload && Application.isPlaying;
#else
                    var asyncUpload = AsyncUpload;
#endif
                    m_renderer.BindGsplatAsset(GsplatAsset, asyncUpload);
                    GsplatSorter.Instance.MarkGlobalBuffersDirty();
                }
            }

            if (Valid && GsplatSettings.Instance.Valid && GsplatSorter.Instance.Valid)
            {
                if (SortMode == GsplatSortMode.OnCameraMove)
                    m_renderer.EvaluateRefreshOnMove();
                else
                    m_renderer.EvaluateRefreshRequired(SortMode, SortRefreshRate - 1, CutoutsRefreshRate - 1);
                m_renderer.DispatchInitOrder(Cutouts, transform.localToWorldMatrix, CutoutsUpdateBounds);
                // When the global sorter has merged all renderers into a single draw call,
                // skip the per-renderer draw — GsplatSorter.DrawAll handles rendering.
                if (!GsplatSorter.Instance.GlobalRenderEnabled)
                {
                    m_renderer.Render(transform, gameObject.layer, GammaToLinear, SHDegree, Brightness,
                        1.0f - SplatDownscaleFactor, RenderOrder, AntiAlias);
                    if (DepthPrepass)
                        m_renderer.RenderDepthPrepass(transform, gameObject.layer, DepthPrepassAlpha,
                            DepthPrepassBiasRel, DepthPrepassBiasAbs);
                }
            }
        }
    }
}