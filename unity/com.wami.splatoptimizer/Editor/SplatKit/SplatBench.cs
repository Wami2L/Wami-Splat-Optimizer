// SplatKit — batch-mode benchmark of Gaussian Splatting scenes (images + timings).
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Banco de escenas Gaussian Splatting / Gaussian Splatting scene bench. Carga PLY desde disco (sin importarlos),
    /// renderiza desde cámaras fijas a PNG y mide el tiempo por cuadro. Cámaras: `splatkit camaras`.
    ///
    ///   Unity.exe -batchmode -projectPath . -executeMethod SplatKit.EditorTools.SplatBench.Run
    ///     -benchPly "ref=C:/.../a.ply;partido=C:/.../p0.ply|C:/.../p1.ply" -benchCams C:/.../camaras.json
    ///     -benchOut C:/.../bench [-benchRes 1440x1584] [-benchRuns 10] [-benchSh 3] [-benchCoords RDF]
    ///     [-benchPiramide manifiesto.json -benchPresupuestos 600000,150000 [-benchNivelMinimo 1]]
    ///     [-benchProfile pcvr_low] [-benchLegacy] [-benchAA] [-benchScale 0.7] [-benchLabel x]
    ///     [-benchRecorrido 24]   (además mide un recorrido en movimiento: 24 cuadros entre cada par de vistas)
    ///   (sin -quit: el banco avanza un paso por tick del editor y sale solo)
    ///
    /// -benchLegacy reproduce gsplat original: ordenar cada cuadro, claves de 32 bits por el eje de la vista, sin
    /// contribución mínima ni modo VR estable. Con cámaras "marco": "unity" (splatkit camaras --lichtfeld) la escena se
    /// coloca como en Unity: en el origen con la rotación de nivelación del manifiesto.
    ///
    /// Cada render va en su propio tick del editor: gsplat dibuja con Graphics.RenderMeshPrimitives, cuya cola solo
    /// se vacía al acabar el frame (en un bucle síncrono los dibujos se acumulan).
    /// </summary>
    public static class SplatBench
    {
        [Serializable] class View { public string nombre; public float[] pos; public float[] mirar; }
        [Serializable] class Cams { public float fov_v = 90; public string marco; public View[] vistas; }
        [Serializable] class Manifest { public float[] traslacion_restada; public float[] rotacion_unity; }
        [Serializable] class PiramideRef { public string ply; }

        [Serializable] class ViewResult
        {
            public string vista;
            public float ms_mediana, ms_min, ms_max;
            public int splats_dibujados;
            public float angulo_grados;
        }

        [Serializable] class PathResult
        {
            public int cuadros, ordenaciones, selecciones_subidas, dibujos;
            public float ms_p50, ms_p95, ms_p99, ms_max, ms_media;
        }

        [Serializable] class Result
        {
            public string etiqueta, ply, coords, dispositivo, gpu, perfil;
            public bool legacy, antialias;
            public float escala = 1f;
            public int splats, sh, ancho, alto, repeticiones;
            public float carga_s, subida_s, control_primera_vista_ms;
            public List<ViewResult> vistas = new List<ViewResult>();
            public PathResult recorrido;
        }

        static IEnumerator<object> s_steps;

        public static void Run()
        {
            s_steps = Steps();
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (s_steps.MoveNext()) return;
                EditorApplication.update -= Tick;
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.update -= Tick;
                EditorApplication.Exit(1);
            }
        }

        static string Arg(string name, string def = null)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }

        static bool Flag(string name) => Environment.GetCommandLineArgs().Contains(name);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>Ajustes del renderer y del LOD según el perfil o el modo legacy.</summary>
        static void Configure(GsplatRenderer gs, SplatLod lod, SplatRuntimeSettings s, bool legacy, bool aa, int sh)
        {
            gs.GammaToLinear = true;        // proyecto lineal, splats entrenados en gamma
            gs.AntiAlias = aa;
            gs.SHDegree = sh;
            if (legacy)
            {
                gs.SortMode = GsplatRenderer.GsplatSortMode.Always;
                gs.SortKey = GsplatRenderer.SortKeyMode.View;
                gs.SortKeyBits = 32;
                GsplatQuality.MinPixelSize = 2f;
                GsplatQuality.MinContribution = 0f;
                if (lod) lod.stableXr = false;
                return;
            }
            gs.SortMode = GsplatRenderer.GsplatSortMode.OnCameraMove;
            if (s == null) return;
            gs.SHDegree = Math.Min(sh, s.sh_degree);
            gs.DepthPrepass = s.depth_prepass;
            gs.SortKey = s.sort_mode == "radial" ? GsplatRenderer.SortKeyMode.Radial : GsplatRenderer.SortKeyMode.View;
            gs.SortKeyBits = s.sort_key_bits;
            GsplatQuality.MinPixelSize = s.min_pixel_size;
            GsplatQuality.MinContribution = s.min_contribution;
            if (lod)
            {
                lod.angleDeg = s.lod_angle_deg;
                lod.stableXr = s.stable_xr_lod;
                lod.behindPenalty = s.behind_penalty;
                lod.periphery = s.periphery;
            }
        }

        static void Place(GameObject go, string manifestJson, SourceCoordinates coords, bool unityFrame)
        {
            if (manifestJson == null) return;
            var m = JsonUtility.FromJson<Manifest>(manifestJson);
            if (unityFrame)
            {
                // Como en las escenas de Unity: en el origen, con la rotación de nivelación.
                var r = m.rotacion_unity;
                if (r != null && r.Length == 4) go.transform.rotation = new Quaternion(r[0], r[1], r[2], r[3]);
                return;
            }
            // Variantes trasladadas (suelo en 0): se deshace para que sirvan las cámaras del original.
            var t = m.traslacion_restada;
            if (t != null && t.Length == 3)
                go.transform.position = coords == SourceCoordinates.RDF ? new Vector3(t[0], -t[1], t[2])
                                                                         : new Vector3(t[0], t[1], t[2]);
        }

        static IEnumerator<object> Steps()
        {
            var plys = (Arg("-benchPly") ?? "")
                .Split(';').Where(s => s.Trim().Length > 0)
                .Select(s => s.Contains('=') ? (s.Split('=')[0].Trim(), s.Substring(s.IndexOf('=') + 1).Trim())
                                             : (Path.GetFileNameWithoutExtension(s.Trim()), s.Trim()))
                .ToList();
            var cams = JsonUtility.FromJson<Cams>(File.ReadAllText(Arg("-benchCams") ?? throw new ArgumentException("falta -benchCams / missing -benchCams")));
            var unityFrame = cams.marco == "unity";
            var outDir = Arg("-benchOut") ?? throw new ArgumentException("falta -benchOut / missing -benchOut");
            var res = Arg("-benchRes", "1440x1584").Split('x').Select(int.Parse).ToArray();
            int runs = int.Parse(Arg("-benchRuns", "10"));
            int sh = int.Parse(Arg("-benchSh", "3"));
            var coords = (SourceCoordinates)Enum.Parse(typeof(SourceCoordinates), Arg("-benchCoords", "RDF"));
            var legacy = Flag("-benchLegacy");
            var aa = Flag("-benchAA");
            var profileId = Arg("-benchProfile");
            var profile = profileId != null ? SplatProfileSet.Builtin.Get(profileId) ??
                                              throw new ArgumentException("perfil desconocido / unknown profile " + profileId) : null;
            var settings = profile?.runtime;
            var scale = F(Arg("-benchScale", settings != null ? settings.splat_resolution.ToString(CultureInfo.InvariantCulture) : "1"));
            var pathFrames = int.Parse(Arg("-benchRecorrido", "0"));
            var labelArg = Arg("-benchLabel");
            var depthArg = Arg("-benchDepth");
            var depth = depthArg?.Split(',').Select(F).ToArray();
            void ApplyDepth(GsplatRenderer r)
            {
                if (depth == null) return;
                r.DepthPrepass = true;
                r.DepthPrepassAlpha = depth[0];
                r.DepthPrepassBiasRel = depth.Length > 1 ? depth[1] : 0.03f;
                r.DepthPrepassBiasAbs = depth.Length > 2 ? depth[2] : 0.03f;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camGo = new GameObject("BenchCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = cams.fov_v;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;
            cam.allowMSAA = false;
            cam.enabled = false;     // solo la renderiza el banco (cam.Render), no el bucle del editor
            // Escala de resolución: se dibuja en un RT reducido y se amplía (bilineal) al tamaño final para la captura.
            var w = Mathf.Max(16, Mathf.RoundToInt(res[0] * scale));
            var h = Mathf.Max(16, Mathf.RoundToInt(res[1] * scale));
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var full = scale < 0.999f ? new RenderTexture(res[0], res[1], 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) : rt;
            cam.targetTexture = rt;
            var tex = new Texture2D(res[0], res[1], TextureFormat.RGB24, false);

            void Pose(View v)
            {
                cam.transform.position = new Vector3(v.pos[0], v.pos[1], v.pos[2]);
                cam.transform.LookAt(new Vector3(v.mirar[0], v.mirar[1], v.mirar[2]), Vector3.up);
            }

            void Save(string path)
            {
                if (full != rt) Graphics.Blit(rt, full);
                RenderTexture.active = full;
                tex.ReadPixels(new Rect(0, 0, full.width, full.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }

            Result NewResult(string label, string ply) => new Result
            {
                etiqueta = label, ply = ply, coords = coords.ToString(), sh = sh, ancho = res[0], alto = res[1],
                repeticiones = runs, dispositivo = SystemInfo.graphicsDeviceType.ToString(),
                gpu = SystemInfo.graphicsDeviceName, perfil = profileId, legacy = legacy, antialias = aa, escala = scale,
            };

            string Suffix() => (legacy ? "_legacy" : "") + (profileId != null ? "_" + profileId : "") +
                               (aa ? "_aa" : "") + (depth != null ? "_dp" : "");

            // Un render medido: lo que haría el player loop (GsplatPlayerLoopHook + Update de cada renderer, que encola
            // el dibujo), el LOD, la cámara, y esperar a la GPU.
            double RenderOnce(GsplatRenderer[] renderers, SplatLod lod)
            {
                GsplatSorter.Instance.Update();
                if (lod) lod.Refresh(cam, false);
                foreach (var gs in renderers) gs.Tick();
                var t = Stopwatch.StartNew();
                cam.Render();
                AsyncGPUReadback.Request(rt).WaitForCompletion();
                return t.Elapsed.TotalMilliseconds;
            }

            IEnumerator<object> MeasureViews(Result r, string dir, GsplatRenderer[] renderers, SplatLod lod)
            {
                foreach (var v in cams.vistas)
                {
                    Pose(v);
                    if (lod) lod.Refresh(cam, true);
                    var times = new List<double>();
                    for (int i = 0; i < runs + 2; i++)
                    {
                        // Como en un cuadro real con la cámara quieta: en modo legacy se reordena igualmente.
                        var ms = RenderOnce(renderers, lod);
                        if (i > 1) times.Add(ms);  // las dos primeras calientan (y ordenan)
                        if (i < runs + 1) yield return null;
                    }
                    times.Sort();
                    r.vistas.Add(new ViewResult
                    {
                        vista = v.nombre, ms_mediana = (float)times[times.Count / 2], ms_min = (float)times[0],
                        ms_max = (float)times[times.Count - 1], splats_dibujados = lod ? lod.SelectedSplats : r.splats,
                        angulo_grados = lod ? lod.EffectiveAngleDeg : 0,
                    });
                    Save(Path.Combine(dir, v.nombre + ".png"));
                    Debug.Log(string.Format(CultureInfo.InvariantCulture, "[SplatBench] {0}/{1}: {2:F2} ms (min {3:F2}){4}",
                        r.etiqueta, v.nombre, times[times.Count / 2], times[0],
                        lod ? $", {lod.SelectedSplats:N0} splats, {lod.EffectiveAngleDeg:F3}°" : ""));
                    yield return null;
                }
            }

            // Recorrido en movimiento: interpola posición y orientación entre vistas consecutivas (como andar y girar
            // la cabeza). Mide cada cuadro y cuenta reordenaciones y selecciones subidas.
            IEnumerator<object> MeasurePath(Result r, GsplatRenderer[] renderers, SplatLod lod)
            {
                if (pathFrames <= 0 || cams.vistas.Length < 2) yield break;
                var times = new List<double>();
                var sorts0 = GsplatSorter.SortDispatches;
                var draws0 = GsplatRendererImpl.DrawSubmissions;
                var uploads0 = lod ? lod.SelectionUploads : 0;
                for (int k = 0; k + 1 < cams.vistas.Length; k++)
                {
                    var a = cams.vistas[k];
                    var b = cams.vistas[k + 1];
                    Pose(a);
                    var pa = cam.transform.position; var qa = cam.transform.rotation;
                    Pose(b);
                    var pb = cam.transform.position; var qb = cam.transform.rotation;
                    for (int f = 0; f < pathFrames; f++)
                    {
                        var t = (f + 1f) / pathFrames;
                        cam.transform.SetPositionAndRotation(Vector3.Lerp(pa, pb, t), Quaternion.Slerp(qa, qb, t));
                        times.Add(RenderOnce(renderers, lod));
                        yield return null;
                    }
                }
                var sorted = times.OrderBy(x => x).ToList();
                float P(float q) => (float)sorted[Mathf.Clamp(Mathf.RoundToInt(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
                r.recorrido = new PathResult
                {
                    cuadros = times.Count, ordenaciones = GsplatSorter.SortDispatches - sorts0,
                    selecciones_subidas = (lod ? lod.SelectionUploads : 0) - uploads0,
                    dibujos = GsplatRendererImpl.DrawSubmissions - draws0,
                    ms_p50 = P(0.5f), ms_p95 = P(0.95f), ms_p99 = P(0.99f), ms_max = (float)sorted[^1],
                    ms_media = (float)times.Average(),
                };
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[SplatBench] {0} recorrido: {1} cuadros, p50 {2:F2} · p95 {3:F2} · p99 {4:F2} ms, {5} ordenaciones, {6} selecciones, {7} dibujos",
                    r.etiqueta, r.recorrido.cuadros, r.recorrido.ms_p50, r.recorrido.ms_p95, r.recorrido.ms_p99,
                    r.recorrido.ordenaciones, r.recorrido.selecciones_subidas, r.recorrido.dibujos));
            }

            foreach (var (label0, path) in plys)
            {
                var label = (labelArg ?? label0) + (labelArg == null ? Suffix() : "");
                var dir = Path.Combine(outDir, label);
                Directory.CreateDirectory(dir);
                var r = NewResult(label, path);

                // Una escena puede venir en varias partes ("a.ply|b.ply"): con más de una, ordenación global.
                var parts = path.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                GsplatSettings.Instance.EnableGlobalSort = parts.Length > 1;
                var gos = new List<GameObject>();
                var assets = new List<GsplatAsset>();
                var sw = Stopwatch.StartNew();
                foreach (var part in parts)
                {
                    var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
                    asset.LoadFromPly(part, null, coords);
                    assets.Add(asset);
                    r.splats += (int)asset.SplatCount;
                    var go = new GameObject("Splats " + Path.GetFileNameWithoutExtension(part));
                    var manPath = Path.ChangeExtension(part, ".json");
                    Place(go, File.Exists(manPath) ? File.ReadAllText(manPath) : null, coords, unityFrame);
                    var gs = go.AddComponent<GsplatRenderer>();
                    gs.ExternalTick = true;
                    gs.GsplatAsset = asset;
                    Configure(gs, null, settings, legacy, aa, sh);
                    ApplyDepth(gs);
                    gos.Add(go);
                }
                r.carga_s = (float)sw.Elapsed.TotalSeconds;
                var renderers = gos.Select(g => g.GetComponent<GsplatRenderer>()).ToArray();
                sw.Restart();
                foreach (var gs in renderers) gs.Tick();   // crea buffers y sube los datos (síncrono fuera de Play)
                r.subida_s = (float)sw.Elapsed.TotalSeconds;
                Debug.Log($"[SplatBench] {label}: {r.splats:N0} splats en {parts.Length} parte(s), carga {r.carga_s:F1} s, subida {r.subida_s:F1} s");
                yield return null;

                var e = MeasureViews(r, dir, renderers, null);
                while (e.MoveNext()) yield return null;
                Pose(cams.vistas[0]);
                r.control_primera_vista_ms = (float)RenderOnce(renderers, null);
                if (r.control_primera_vista_ms > 1.5f * r.vistas[0].ms_mediana + 2f && legacy)
                    Debug.LogWarning($"[SplatBench] {label}: la primera vista ahora tarda {r.control_primera_vista_ms:F1} ms " +
                                     $"(antes {r.vistas[0].ms_mediana:F1}): ¿se acumulan dibujos?");
                e = MeasurePath(r, renderers, null);
                while (e.MoveNext()) yield return null;
                File.WriteAllText(Path.Combine(dir, "tiempos.json"), JsonUtility.ToJson(r, true));
                yield return null;

                foreach (var go in gos) UnityEngine.Object.DestroyImmediate(go);
                foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset);
                GC.Collect();
                yield return null;
            }

            // Modo pirámide (LOD dinámico): un asset con todos los niveles y SplatLod eligiendo por vista.
            var pirPath = Arg("-benchPiramide");
            if (pirPath != null)
            {
                var json = File.ReadAllText(pirPath);
                GsplatSettings.Instance.EnableGlobalSort = false;
                var sw = Stopwatch.StartNew();
                var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
                var plyPath = JsonUtility.FromJson<PiramideRef>(json).ply;
                if (!File.Exists(plyPath)) plyPath = Path.ChangeExtension(pirPath, ".ply");   // manifiesto copiado
                asset.LoadFromPly(plyPath, null, coords);
                var carga = (float)sw.Elapsed.TotalSeconds;
                var go = new GameObject("Piramide");
                Place(go, json, coords, unityFrame);
                var gs = go.AddComponent<GsplatRenderer>();
                gs.ExternalTick = true;
                gs.GsplatAsset = asset;
                var lod = go.AddComponent<SplatLod>();
                lod.Load(json);
                lod.targetCamera = cam;
                Configure(gs, lod, settings, legacy, aa, sh);
                ApplyDepth(gs);
                sw.Restart();
                gs.Tick();
                var subida = (float)sw.Elapsed.TotalSeconds;
                Debug.Log($"[SplatBench] pirámide: {asset.SplatCount:N0} splats, carga {carga:F1} s, subida {subida:F1} s");
                yield return null;

                var angulo = Arg("-benchAngulo");
                var nivelMin = int.Parse(Arg("-benchNivelMinimo", "0"));
                var budgets = Arg("-benchPresupuestos", settings != null ? settings.splat_budget.ToString() : "1500000")
                    .Split(',').Select(int.Parse);
                foreach (var presupuesto in budgets)
                {
                    var label = labelArg != null ? $"{labelArg}_{presupuesto / 1000}k"
                        : $"pir_{presupuesto / 1000}k" + (nivelMin > 0 ? $"_n{nivelMin}" : "") + Suffix();
                    var dir = Path.Combine(outDir, label);
                    Directory.CreateDirectory(dir);
                    var r = NewResult(label, pirPath);
                    r.splats = (int)asset.SplatCount;
                    r.carga_s = carga;
                    r.subida_s = subida;
                    if (angulo != null) lod.angleDeg = F(angulo);
                    else if (settings == null) lod.angleDeg = 0.15f;
                    lod.budget = presupuesto;
                    lod.minLevel = nivelMin;
                    lod.marginFactor = F(Arg("-benchMargen", "3"));
                    if (settings == null) lod.periphery = F(Arg("-benchPeriferia", "1"));
                    var e = MeasureViews(r, dir, new[] { gs }, lod);
                    while (e.MoveNext()) yield return null;
                    e = MeasurePath(r, new[] { gs }, lod);
                    while (e.MoveNext()) yield return null;
                    File.WriteAllText(Path.Combine(dir, "tiempos.json"), JsonUtility.ToJson(r, true));
                    yield return null;
                }
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(asset);
            }
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            if (full != rt) UnityEngine.Object.DestroyImmediate(full);
        }
    }
}
