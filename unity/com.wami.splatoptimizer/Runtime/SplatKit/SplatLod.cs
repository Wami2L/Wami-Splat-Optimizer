// SplatKit — dynamic LOD over a chunked splat pyramid (splatkit piramide / preparar).
// SPDX-License-Identifier: MIT

using System;
using Gsplat;
using UnityEngine;
using UnityEngine.Serialization;

namespace SplatKit
{
    /// <summary>
    /// LOD dinámico de un entorno Gaussian Splatting completo, como el LOD por octree de PlayCanvas. El GsplatRenderer
    /// tiene UN asset con todos los niveles de todos los trozos (splatkit piramide) y este componente decide qué
    /// dibujar: por trozo, el nivel más grueso cuyo vóxel no pase de θ · distancia (tamaño angular constante); si la suma
    /// pasa del presupuesto, θ crece hasta caber.
    ///
    /// Dynamic LOD of a whole Gaussian Splatting environment (PlayCanvas-style octree LOD): per chunk, the coarsest
    /// level whose voxel stays under θ · distance; θ grows until the selection fits the splat budget.
    ///
    /// <para><b>Modo VR estable / stable XR mode</b> (<see cref="stableXr"/>): recorte por frustum con un margen ancho
    /// (<see cref="stableFovMargin"/>) cuyos trozos usan un nivel <see cref="behindPenalty"/> veces más grueso, y la
    /// selección solo se rehace al moverse o al girar más de <see cref="stableTurnToRefresh"/>: girar la cabeza ya no
    /// sube índices ni obliga a reordenar cada pocos grados. / Wide-margin frustum cull, coarser margin chunks and
    /// re-selection only after moving or turning far.</para>
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(GsplatRenderer))]
    [AddComponentMenu("Wami Splat Optimizer/Splat LOD")]
    public class SplatLod : MonoBehaviour
    {
        [Serializable]
        public class Trozo
        {
            public float[] min, max;     // caja en ejes de Unity (espacio local del renderer) / local box
            public int[] inicio, cuenta; // rango de cada nivel dentro del asset / range of each level
        }

        [Serializable]
        public class Manifiesto
        {
            public float trozo_m;
            public float[] niveles_voxel_m;
            public int nivel_minimo;
            public int splats_total;
            public Trozo[] trozos;
        }

        [Tooltip("Manifiesto JSON de la pirámide / pyramid manifest (splatkit piramide)")]
        [FormerlySerializedAs("manifiesto")] public TextAsset manifest;
        [Tooltip("Cámara que decide el LOD (por defecto Camera.main) / camera driving the LOD")]
        [FormerlySerializedAs("camara")] public Camera targetCamera;
        [Tooltip("Tamaño angular objetivo de un vóxel (grados). Menor = más detalle / target voxel angular size")]
        [FormerlySerializedAs("anguloGrados")] public float angleDeg = 0.15f;
        [Tooltip("Máximo de splats dibujados (0 = sin límite) / max drawn splats (0 = unlimited)")]
        [FormerlySerializedAs("presupuesto")] public int budget = 1_500_000;
        [Tooltip("Nivel más fino permitido (1 = sin los splats originales) / finest level allowed")]
        [FormerlySerializedAs("nivelMinimo")] public int minLevel;

        [Tooltip("VR: margen de giro ancho y no rehacer la selección cada pocos grados / wide turn margin, " +
                 "no re-selection every few degrees")]
        public bool stableXr;
        [Tooltip("Modo VR estable: factor del FOV del margen / stable XR: FOV factor of the margin")]
        public float stableFovMargin = 1.8f;
        [Tooltip("Modo VR estable: los trozos del margen usan un ángulo este factor mayor / stable XR: margin chunks " +
                 "use this factor coarser angle")]
        public float behindPenalty = 1.5f;
        [Tooltip("Modo VR estable: girar más que esto (grados) rehace la selección / stable XR: re-select after " +
                 "turning more than this")]
        public float stableTurnToRefresh = 20f;

        [FormerlySerializedAs("recortarFrustum")] public bool frustumCull = true;
        [Tooltip("Factor del FOV para el recorte (margen para giros) / FOV factor for culling")]
        [FormerlySerializedAs("margenFov")] public float fovMargin = 1.3f;
        [Tooltip("Los trozos del margen usan un ángulo este factor mayor / margin chunks use this factor coarser angle")]
        [FormerlySerializedAs("factorMargen")] public float marginFactor = 3f;
        [Tooltip("Cuánto crece el ángulo hacia el borde de la vista (foveación) / angle growth toward the view edge")]
        [FormerlySerializedAs("periferia")] public float periphery = 1f;
        [FormerlySerializedAs("distanciaMinima")] public float minDistance = 0.5f;
        [FormerlySerializedAs("moverParaRecalcular")] public float moveToRefresh = 0.05f;
        [FormerlySerializedAs("girarParaRecalcular")] public float turnToRefresh = 2f;

        /// <summary>
        /// No dibujar nada sin soltar los datos de la GPU (desactivar el GsplatRenderer los libera y volver a subirlos
        /// cuesta segundos). / Draw nothing while keeping the GPU data.
        /// </summary>
        public bool Hidden
        {
            get => m_hidden;
            set { if (m_hidden != value) { m_hidden = value; m_dirty = true; } }
        }

        public int SelectedSplats { get; private set; }
        public int VisibleChunks { get; private set; }
        public float EffectiveAngleDeg { get; private set; }
        /// <summary>Veces que se ha subido una selección nueva / how many selections were uploaded.</summary>
        public int SelectionUploads { get; private set; }
        public Manifiesto Data => m_manifest;

        Manifiesto m_manifest;
        Bounds[] m_bounds;
        int[] m_choice, m_prevChoice;
        float[] m_dist, m_weight;
        bool[] m_inView;
        uint[] m_indices = Array.Empty<uint>();
        Vector3 m_lastPos;
        Quaternion m_lastRot;
        bool m_dirty = true;
        bool m_hidden;
        GsplatRenderer m_gs;

        public void Load(string json)
        {
            m_manifest = JsonUtility.FromJson<Manifiesto>(json);
            var n = m_manifest.trozos.Length;
            m_bounds = new Bounds[n];
            for (int i = 0; i < n; i++)
            {
                var t = m_manifest.trozos[i];
                var b = new Bounds();
                b.SetMinMax(new Vector3(t.min[0], t.min[1], t.min[2]), new Vector3(t.max[0], t.max[1], t.max[2]));
                m_bounds[i] = b;
            }
            m_choice = new int[n];
            m_prevChoice = new int[n];
            m_dist = new float[n];
            m_weight = new float[n];
            m_inView = new bool[n];
            for (int i = 0; i < n; i++) m_prevChoice[i] = int.MinValue;
            m_dirty = true;
        }

        /// <summary>Marca la selección para recalcular (p. ej. tras cambiar el presupuesto). / Mark for re-selection.</summary>
        public void SetDirty() => m_dirty = true;

        void OnEnable()
        {
            m_gs = GetComponent<GsplatRenderer>();
            if (manifest) Load(manifest.text);
        }

        void OnValidate() => m_dirty = true;

        Camera ActiveCamera()
        {
            if (targetCamera) return targetCamera;
#if UNITY_EDITOR
            if (!Application.isPlaying && UnityEditor.SceneView.lastActiveSceneView)
                return UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            return Camera.main;
        }

        void LateUpdate()
        {
            var cam = ActiveCamera();
            if (cam) Refresh(cam, false);
        }

        int Level(float distance, float theta)
        {
            var levels = m_manifest.niveles_voxel_m;
            var allowed = theta * Mathf.Max(distance, minDistance);
            int k = 0;                                   // nivel 0 = splats originales / level 0 = original splats
            for (int j = 1; j < levels.Length; j++)
                if (levels[j] <= allowed) k = j;
            return Mathf.Max(k, minLevel);
        }

        /// <summary>Recalcula la selección para la cámara. / Recompute the selection for the camera.</summary>
        public int Refresh(Camera cam, bool force)
        {
            // Carga perezosa: al crear el componente por código, OnEnable corre antes de asignar el manifiesto.
            if (m_manifest == null && manifest) Load(manifest.text);
            if (!m_gs) m_gs = GetComponent<GsplatRenderer>();
            if (m_manifest == null || m_gs == null) return 0;
            if (m_hidden)
            {
                if (m_dirty && m_gs.SetSelection(m_indices, 0))
                {
                    m_dirty = false;
                    SelectedSplats = 0;
                    for (int i = 0; i < m_prevChoice.Length; i++) m_prevChoice[i] = int.MinValue;
                }
                return 0;
            }
            var tr = cam.transform;
            var turn = stableXr ? stableTurnToRefresh : turnToRefresh;
            if (!force && !m_dirty &&
                (tr.position - m_lastPos).sqrMagnitude < moveToRefresh * moveToRefresh &&
                Quaternion.Angle(tr.rotation, m_lastRot) < turn)
                return SelectedSplats;

            var camLocal = transform.InverseTransformPoint(tr.position);
            Plane[] planes = null, core = null;
            var halfFov = Mathf.Max(cam.fieldOfView * 0.5f,
                                    Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect) * Mathf.Rad2Deg);
            if (frustumCull)
            {
                var margin = stableXr ? Mathf.Max(fovMargin, stableFovMargin) : fovMargin;
                core = GeometryUtility.CalculateFrustumPlanes(cam);
                // FOV vertical y horizontal ampliados cada uno por fovMargin; el aspecto sale de los dos.
                var halfV = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
                var halfH = Mathf.Atan(Mathf.Tan(halfV) * cam.aspect);
                var wideV = Mathf.Min(halfV * margin, 85f * Mathf.Deg2Rad);
                var wideH = Mathf.Min(halfH * margin, 85f * Mathf.Deg2Rad);
                var proj = Matrix4x4.Perspective(2 * wideV * Mathf.Rad2Deg, Mathf.Tan(wideH) / Mathf.Tan(wideV),
                                                 cam.nearClipPlane, cam.farClipPlane);
                planes = GeometryUtility.CalculateFrustumPlanes(proj * cam.worldToCameraMatrix);
            }

            var trozos = m_manifest.trozos;
            int visibles = 0;
            for (int i = 0; i < trozos.Length; i++)
            {
                m_dist[i] = Mathf.Sqrt(m_bounds[i].SqrDistance(camLocal));
                m_weight[i] = 1f;
                var inView = true;
                if (m_dist[i] > 0)
                {
                    var wb = WorldBounds(m_bounds[i]);
                    if (planes != null)
                    {
                        inView = GeometryUtility.TestPlanesAABB(planes, wb);
                        if (inView && !GeometryUtility.TestPlanesAABB(core, wb))
                            m_weight[i] = stableXr ? behindPenalty : marginFactor;
                    }
                    if (periphery > 0)
                    {
                        // Ángulo entre el eje de la vista y el punto del trozo más cercano a él.
                        var toward = wb.ClosestPoint(tr.position + tr.forward * m_dist[i]) - tr.position;
                        var ang = Vector3.Angle(tr.forward, toward);
                        var t = Mathf.Clamp01((ang - 15f) / Mathf.Max(halfFov - 15f, 1f));
                        m_weight[i] *= 1f + periphery * t * t;
                    }
                }
                m_inView[i] = inView;
                if (inView) visibles++;
            }

            // Ángulo objetivo; si no cabe en el presupuesto, el menor ángulo que cabe (bisección en escala log: la
            // selección total es monótona en θ). / Smallest angle that fits the budget (log bisection).
            var theta = angleDeg * Mathf.Deg2Rad;
            var total = Select(theta);
            if (budget > 0 && total > budget)
            {
                float lo = Mathf.Max(theta, 1e-5f), hi = lo;
                while (Select(hi) > budget && hi < Mathf.PI) { lo = hi; hi *= 2f; }
                for (int iter = 0; iter < 18; iter++)
                {
                    var mid = Mathf.Sqrt(lo * hi);
                    if (Select(mid) > budget) lo = mid; else hi = mid;
                }
                theta = hi;
                total = Select(theta);
            }
            EffectiveAngleDeg = theta * Mathf.Rad2Deg;
            VisibleChunks = visibles;
            m_lastPos = tr.position;
            m_lastRot = tr.rotation;

            var changed = m_dirty;
            for (int i = 0; i < trozos.Length && !changed; i++)
                changed = m_choice[i] != m_prevChoice[i];
            if (!changed) return SelectedSplats;

            var count = (int)Math.Min(total, int.MaxValue);
            if (m_indices.Length < count) m_indices = new uint[Math.Max(count, m_indices.Length * 3 / 2)];
            int c = 0;
            for (int i = 0; i < trozos.Length; i++)
            {
                var k = m_choice[i];
                if (k < 0) continue;
                var start = (uint)trozos[i].inicio[k];
                var n = trozos[i].cuenta[k];
                for (int j = 0; j < n; j++) m_indices[c++] = start + (uint)j;
            }
            if (!m_gs.SetSelection(m_indices, c))                               // aún sin subir: reintentar luego
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                return SelectedSplats;
            }
            Array.Copy(m_choice, m_prevChoice, m_choice.Length);
            m_dirty = false;
            SelectedSplats = c;
            SelectionUploads++;
            return c;
        }

        /// <summary>Elige nivel por trozo para el ángulo theta (rad) y devuelve el total de splats.</summary>
        long Select(float theta)
        {
            var trozos = m_manifest.trozos;
            long total = 0;
            for (int i = 0; i < trozos.Length; i++)
            {
                if (!m_inView[i]) { m_choice[i] = -1; continue; }
                var k = Level(m_dist[i], theta * m_weight[i]);
                m_choice[i] = k;
                total += trozos[i].cuenta[k];
            }
            return total;
        }

        Bounds WorldBounds(Bounds local)
        {
            if (transform.rotation == Quaternion.identity && transform.lossyScale == Vector3.one)
                return new Bounds(local.center + transform.position, local.size);
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) == 0 ? local.min.x : local.max.x, (i & 2) == 0 ? local.min.y : local.max.y,
                                    (i & 4) == 0 ? local.min.z : local.max.z);
                var w = transform.TransformPoint(p);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }
    }
}
