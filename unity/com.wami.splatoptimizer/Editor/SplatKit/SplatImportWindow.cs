// Wami Splat Optimizer — import a 3DGS scene (.ply) into the project: optimize, LOD, collisions and scenes.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Ejecuta `python -m splatkit preparar` como proceso aparte y, al terminar, mete el resultado en la carpeta de
    /// entornos y crea sus escenas. El estado vive en SessionState: sobrevive a las recargas de dominio y a cerrar la
    /// ventana. `preparar` escribe en Temp/ del proyecto (Unity no lo importa: nada de PLY a medio copiar; cancelar no
    /// deja nada en Assets).
    ///
    /// Runs `python -m splatkit preparar` as a separate process, then moves the result into the environments folder
    /// and creates its scenes. Survives domain reloads.
    /// </summary>
    [InitializeOnLoad]
    public static class SplatImport
    {
        [Serializable]
        public class Request
        {
            public string ply = "", nombre = "", colmap = "";
            public float alturaCamara = 1.5f, escala, trozo, gigante = 0.05f, agujas = 10f;
            public int sh = 1, nivelBajo = 1, vecinos = 16;
            public bool vr = true, antialias;
            public bool low = true, high = true, max;            // calidades a exportar (todas con LOD)

            public string Pyramids => string.Join(",", new[] { low ? "low" : null, high ? "high" : null, max ? "max" : null }
                                                           .Where(x => x != null));
        }

        const string K = "WamiSplatOptimizer.Import.";
        static int Pid { get => SessionState.GetInt(K + "pid", 0); set => SessionState.SetInt(K + "pid", value); }
        static int ProgressId { get => SessionState.GetInt(K + "progress", -1); set => SessionState.SetInt(K + "progress", value); }
        static bool Cancelled { get => SessionState.GetBool(K + "cancelled", false); set => SessionState.SetBool(K + "cancelled", value); }
        static bool WithVr { get => SessionState.GetBool(K + "vr", true); set => SessionState.SetBool(K + "vr", value); }
        public static string Name { get => SessionState.GetString(K + "name", ""); private set => SessionState.SetString(K + "name", value); }
        public static string Step { get => SessionState.GetString(K + "step", ""); private set => SessionState.SetString(K + "step", value); }
        public static float Fraction { get => SessionState.GetFloat(K + "fraction", 0); private set => SessionState.SetFloat(K + "fraction", value); }
        static long Start { get => long.TryParse(SessionState.GetString(K + "start", "0"), out var t) ? t : 0;
                            set => SessionState.SetString(K + "start", value.ToString(CultureInfo.InvariantCulture)); }

        static string Staging => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "WamiSplatImport");
        static string LogPath => Path.Combine(Staging, "preparar.log");
        static bool Windows => Application.platform == RuntimePlatform.WindowsEditor;

        public static bool Running => Pid != 0;
        public static TimeSpan Elapsed => Start > 0 ? DateTime.Now - new DateTime(Start) : TimeSpan.Zero;
        public static event Action Changed;

        static readonly StringBuilder s_log = new StringBuilder();
        static long s_read;
        static double s_next;
        static Process s_proc;

        /// <summary>El registro del último `preparar`. / Log of the last run.</summary>
        public static string Log => s_log.ToString();

        static SplatImport()
        {
            ReadLog(true);
            if (!Running) return;
            if (Progress.Exists(ProgressId)) Progress.RegisterCancelCallback(ProgressId, CancelFromProgress);
            EditorApplication.update += Tick;
        }

        static string F(float v) => v.ToString(CultureInfo.InvariantCulture);
        static string Q(string s) => "\"" + s + "\"";

        public static void Launch(Request p)
        {
            if (Running) throw new InvalidOperationException(SplatLang.T("Ya hay una importación en marcha", "An import is already running"));
            var problem = SplatKitPython.Problem();
            if (problem != null) throw new InvalidOperationException(problem);
            if (Directory.Exists(Staging)) Directory.Delete(Staging, true);
            Directory.CreateDirectory(Staging);

            var args = SplatKitPython.CliArgs(Q);
            args.AddRange(new[] { "--lang", SplatLang.Spanish ? "es" : "en" });
            args.AddRange(new[]
            {
                "preparar", Q(p.ply), "--nombre", Q(p.nombre), "--unity", Q(Staging), "--unity-carpeta", Q(SplatEnvironments.Root),
                "--sh", p.sh.ToString(CultureInfo.InvariantCulture), "--nivel-bajo", p.nivelBajo.ToString(CultureInfo.InvariantCulture), "--piramides", p.Pyramids,
                "--agujas", F(p.agujas), "--vecinos", p.vecinos.ToString(CultureInfo.InvariantCulture), "--gigante", F(p.gigante),
            });
            if (!string.IsNullOrEmpty(p.colmap)) args.AddRange(new[] { "--colmap", Q(p.colmap), "--altura-camara", F(p.alturaCamara) });
            if (p.escala > 0) args.AddRange(new[] { "--escala", F(p.escala) });
            if (p.trozo > 0) args.AddRange(new[] { "--trozo", F(p.trozo) });
            if (p.antialias) args.Add("--antialias");
            var line = $"{Q(SplatKitPython.Python)} {string.Join(" ", args)} > {Q(LogPath)} 2>&1";
            var psi = Windows
                ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{line}\"")
                : new ProcessStartInfo("/bin/sh", "-c '" + line.Replace("'", "'\\''") + "'");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = SplatKitPython.PythonDir;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            Debug.Log($"[Wami Splat Optimizer] {line}");

            s_proc = Process.Start(psi) ?? throw new InvalidOperationException(SplatLang.T("No se pudo arrancar Python", "Could not start Python"));
            Pid = s_proc.Id;
            Name = p.nombre;
            WithVr = p.vr;
            Cancelled = false;
            Step = SplatLang.T("Arrancando Python", "Starting Python");
            Fraction = 0;
            Start = DateTime.Now.Ticks;
            s_log.Clear();
            s_read = 0;
            ProgressId = Progress.Start(SplatLang.T("Importar escena 3DGS", "Import 3DGS scene"), p.nombre);
            Progress.RegisterCancelCallback(ProgressId, CancelFromProgress);
            EditorApplication.update += Tick;
            Changed?.Invoke();
        }

        static bool CancelFromProgress()
        {
            Cancel();
            return true;
        }

        public static void Cancel()
        {
            if (!Running) return;
            Cancelled = true;
            try
            {
                // cmd.exe lanza python: hay que matar el árbol entero. / Kill the whole process tree.
                if (Windows)
                    Process.Start(new ProcessStartInfo("taskkill", $"/PID {Pid} /T /F") { UseShellExecute = false, CreateNoWindow = true })
                        ?.WaitForExit(5000);
                else
                    Process.GetProcessById(Pid).Kill();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Wami Splat Optimizer] {Pid}: {e.Message}");
            }
        }

        static bool Alive()
        {
            try
            {
                s_proc ??= Process.GetProcessById(Pid);
                return !s_proc.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < s_next) return;
            s_next = EditorApplication.timeSinceStartup + 0.25;
            ReadLog(false);
            if (Alive()) return;
            EditorApplication.update -= Tick;
            ReadLog(true);
            Finish();
        }

        static readonly Regex s_step = new Regex(@"^\[(\d+)/(\d+)\]\s*(.*)$");

        static void ReadLog(bool toEnd)
        {
            if (!File.Exists(LogPath)) return;
            byte[] buf;
            try
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= s_read) return;
                fs.Seek(s_read, SeekOrigin.Begin);
                buf = new byte[fs.Length - s_read];
                int n = 0;
                while (n < buf.Length)
                {
                    int r = fs.Read(buf, n, buf.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
                if (n < buf.Length) Array.Resize(ref buf, n);
            }
            catch (IOException)
            {
                return;
            }
            int end = toEnd ? buf.Length - 1 : Array.LastIndexOf(buf, (byte)'\n');
            if (end < 0) return;
            var text = Encoding.UTF8.GetString(buf, 0, end + 1).Replace("\r", "");
            s_read += end + 1;
            foreach (var l in text.Split('\n'))
            {
                var m = s_step.Match(l.Trim());
                if (!m.Success) continue;
                int i = int.Parse(m.Groups[1].Value), n = Math.Max(1, int.Parse(m.Groups[2].Value));
                Fraction = (i - 1f) / n;
                Step = $"{i}/{n} · {m.Groups[3].Value}";
                if (Running && Progress.Exists(ProgressId)) Progress.Report(ProgressId, Fraction, Step);
            }
            s_log.Append(text);
            if (s_log.Length > 200_000) s_log.Remove(0, s_log.Length - 150_000);
            Changed?.Invoke();
        }

        static void Finish()
        {
            var name = Name;
            Pid = 0;
            s_proc = null;
            bool ok = !Cancelled && (Log.Contains("Listo en") || Log.Contains("Done in"));
            if (!ok)
            {
                Step = Cancelled ? SplatLang.T("Cancelado: no se ha tocado nada del proyecto", "Cancelled: the project was not touched")
                                 : SplatLang.T("Error: mira el registro", "Error: see the log");
                if (Progress.Exists(ProgressId)) Progress.Finish(ProgressId, Cancelled ? Progress.Status.Canceled : Progress.Status.Failed);
                if (!Cancelled)
                    Debug.LogError($"[Wami Splat Optimizer] preparar:\n{string.Join("\n", Log.Split('\n').Reverse().Take(25).Reverse())}");
                Changed?.Invoke();
                return;
            }
            try
            {
                Step = SplatLang.T("Importando en Unity", "Importing into Unity");
                var folder = Move(name);
                AssetDatabase.Refresh();
                if (Progress.Exists(ProgressId)) Progress.Finish(ProgressId);
                Fraction = 1;
                Step = SplatLang.T($"Listo en {Elapsed:h\\:mm\\:ss}", $"Done in {Elapsed:h\\:mm\\:ss}");
                Debug.Log($"[Wami Splat Optimizer] «{name}» → {folder}");
                CreateScenes(folder, WithVr, null);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Step = SplatLang.T("Error al importar: ", "Import error: ") + e.Message;
                if (Progress.Exists(ProgressId)) Progress.Finish(ProgressId, Progress.Status.Failed);
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Importación sin ventana (batch/CI): -executeMethod SplatKit.EditorTools.SplatImport.BatchImport -ply X.ply
        /// -nombre X [-colmap sparse/0] [-sinVR] (sin -quit). / Headless import.
        /// </summary>
        public static void BatchImport()
        {
            var a = Environment.GetCommandLineArgs();
            string Arg(string n) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            var req = new Request { ply = Arg("-ply") ?? "", nombre = Arg("-nombre") ?? "", colmap = Arg("-colmap") ?? "",
                                    vr = Array.IndexOf(a, "-sinVR") < 0, antialias = Array.IndexOf(a, "-antialias") >= 0 };
            var pyr = Arg("-piramides");
            if (pyr != null) { req.low = pyr.Contains("low"); req.high = pyr.Contains("high"); req.max = pyr.Contains("max"); }
            Launch(req);
            void Wait()
            {
                if (Running) return;
                EditorApplication.update -= Wait;
                var ok = Step.StartsWith("Listo") || Step.StartsWith("Done");
                Debug.Log($"[Wami Splat Optimizer] BatchImport: {Step}");
                EditorApplication.Exit(ok ? 0 : 1);
            }
            EditorApplication.update += Wait;
        }

        /// <summary>Mueve lo que dejó `preparar` en Temp/ a Assets (mismo disco: instantáneo).</summary>
        static string Move(string name)
        {
            var src = Path.Combine(new[] { Staging }.Concat(SplatEnvironments.Root.Split('/')).Append(name).ToArray());
            if (!Directory.Exists(src))
                throw new DirectoryNotFoundException(SplatLang.T($"preparar no dejó nada en {src}", $"preparar left nothing in {src}"));
            var folder = $"{SplatEnvironments.Root}/{name}";
            var dst = Path.Combine(Path.GetDirectoryName(Application.dataPath), folder);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var t = Path.Combine(dst, Path.GetFileName(f));
                // Al reimportar se conserva el .meta (GUID): las escenas siguen apuntando a los mismos assets.
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && File.Exists(t)) continue;
                if (File.Exists(t)) File.Delete(t);
                File.Move(f, t);
            }
            return folder;
        }

        /// <summary>
        /// Crea las escenas que falten (escritorio y, con colisiones y XRI, VR) y abre la de escritorio.
        /// <paramref name="recreate"/>: null = preguntar. / Creates missing scenes; null = ask before recreating.
        /// </summary>
        public static void CreateScenes(string folder, bool vr, bool? recreate)
        {
            if (Application.isBatchMode) recreate ??= true;
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            vr &= File.Exists(folder + "/colision.json") && SplatEnvironments.Vr != null;
            var scenes = new List<(string path, Action create)>
                { (SplatEnvironments.ScenePath(folder), () => SplatEnvironments.CreateDesktopScene(folder)) };
            if (vr) scenes.Add((SplatEnvironments.Vr.ScenePath(folder), () => SplatEnvironments.Vr.Create(folder)));
            var existing = scenes.Where(e => File.Exists(e.path)).Select(e => e.path).ToArray();
            bool redo = recreate ?? existing.Length > 0 && EditorUtility.DisplayDialog(
                SplatLang.T("Importar escena 3DGS", "Import 3DGS scene"),
                SplatLang.T($"Los splats ya están actualizados. Estas escenas ya existían:\n\n{string.Join("\n", existing)}\n\n" +
                            "¿Recrearlas? Se pierde lo que les hayas añadido a mano.",
                            $"The splats are updated. These scenes already existed:\n\n{string.Join("\n", existing)}\n\n" +
                            "Recreate them? Anything you added by hand is lost."),
                SplatLang.T("Recrear", "Recreate"), SplatLang.T("Mantener", "Keep"));
            foreach (var (path, create) in scenes)
                if (redo || !File.Exists(path)) create();
            EditorSceneManager.OpenScene(SplatEnvironments.ScenePath(folder), OpenSceneMode.Single);
        }
    }

    /// <summary>
    /// Tools › Wami Splat Optimizer › Importar escena 3DGS: trae una escena Gaussian Splatting (.ply) al proyecto
    /// optimizada, con LOD, colisiones y escenas listas. / Brings a Gaussian Splatting scene into the project.
    /// </summary>
    public class SplatImportWindow : EditorWindow
    {
        const string KeyRequest = "WamiSplatOptimizer.Import.last";
        static readonly Regex s_validName = new Regex(@"^[A-Za-z0-9_\-]+$");

        SplatImport.Request m_p;
        string m_autoName, m_autoColmap;
        bool m_advanced, m_settings;
        Vector2 m_scroll, m_scrollLog;
        Dictionary<string, string> m_info;

        static string T(string es, string en) => SplatLang.T(es, en);

        [MenuItem("Tools/Wami Splat Optimizer/Importar escena 3DGS · Import 3DGS scene…", priority = 1)]
        public static void Open()
        {
            var w = GetWindow<SplatImportWindow>();
            w.titleContent = new GUIContent("Wami Splat Optimizer");
            w.minSize = new Vector2(480, 460);
        }

        void OnEnable()
        {
            m_p = new SplatImport.Request();
            JsonUtility.FromJsonOverwrite(EditorPrefs.GetString(KeyRequest, "{}"), m_p);
            m_settings = SplatKitPython.Problem() != null;
            SplatImport.Changed += Repaint;
        }

        void OnDisable()
        {
            SplatImport.Changed -= Repaint;
            EditorPrefs.SetString(KeyRequest, JsonUtility.ToJson(m_p));
        }

        void OnFocus() => m_info = null;
        void OnProjectChange() => m_info = null;

        void Update()
        {
            if (SplatImport.Running) Repaint();
        }

        /// <summary>
        /// Carpeta sparse/0 de COLMAP para el PLY: junto a él o en carpetas superiores, incluido el esquema
        /// &lt;flujo&gt;/resultados/&lt;escena&gt;__x.ply ↔ &lt;flujo&gt;/proyectos/&lt;escena&gt;/colmap/sparse/0.
        /// </summary>
        static string GuessColmap(string ply)
        {
            if (!File.Exists(ply)) return "";
            var scene = Path.GetFileNameWithoutExtension(ply).Split(new[] { "__" }, StringSplitOptions.None)[0];
            var d = Path.GetDirectoryName(ply);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(d); i++, d = Path.GetDirectoryName(d))
                foreach (var c in new[]
                         {
                             Path.Combine(d, "sparse", "0"), Path.Combine(d, "colmap", "sparse", "0"),
                             Path.Combine(d, scene, "colmap", "sparse", "0"), Path.Combine(d, "proyectos", scene, "colmap", "sparse", "0"),
                         })
                    if (File.Exists(Path.Combine(c, "images.txt")) || File.Exists(Path.Combine(c, "images.bin")))
                        return c.Replace('\\', '/');
            return "";
        }

        static string NameOf(string ply)
        {
            var scene = Path.GetFileNameWithoutExtension(ply).Split(new[] { "__" }, StringSplitOptions.None)[0];
            if (scene is "scene" or "point_cloud" or "splat")
                scene = Path.GetFileName(Path.GetDirectoryName(ply));
            return Regex.Replace(scene, @"[^A-Za-z0-9_\-]", "_");
        }

        void SetPly(string ply)
        {
            m_p.ply = ply.Replace('\\', '/');
            if (string.IsNullOrEmpty(m_p.nombre) || m_p.nombre == m_autoName) m_p.nombre = m_autoName = NameOf(m_p.ply);
            if (string.IsNullOrEmpty(m_p.colmap) || m_p.colmap == m_autoColmap) m_p.colmap = m_autoColmap = GuessColmap(m_p.ply);
        }

        void OnGUI()
        {
            m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
            bool running = SplatImport.Running;

            EditorGUILayout.LabelField(T("Escena nueva", "New scene"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(T("Limpia la escena, la escala y nivela con las cámaras de COLMAP, construye las pirámides LOD " +
                                      "de PC y de visores autónomos y las colisiones, y crea sus escenas (python -m splatkit preparar).",
                                      "Cleans the scene, scales and levels it with the COLMAP cameras, builds the PC and standalone " +
                                      "headset LOD pyramids and the collisions, and creates its scenes (python -m splatkit preparar)."),
                                    MessageType.None);
            using (new EditorGUI.DisabledScope(running))
            {
                var errors = Form();
                EditorGUILayout.Space();
                using (new EditorGUI.DisabledScope(errors))
                    if (GUILayout.Button(T("Importar", "Import"), GUILayout.Height(28)))
                        DoImport();
            }

            if (running || !string.IsNullOrEmpty(SplatImport.Step)) Status(running);

            EditorGUILayout.Space(10);
            EnvironmentList();

            EditorGUILayout.Space(10);
            m_settings = EditorGUILayout.Foldout(m_settings, T("Ajustes (Python y carpetas)", "Settings (Python and folders)"), true);
            if (m_settings)
            {
                SplatKitPython.SettingsGUI();
                EditorGUILayout.Space(4);
                using (new EditorGUI.IndentLevelScope()) SplatProjectSettings.OnGUI();
            }
            EditorGUILayout.EndScrollView();
        }

        bool Form()
        {
            bool errors = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                var ply = EditorGUILayout.TextField(new GUIContent("PLY 3DGS", T("Escena Gaussian Splatting (.ply, formato INRIA)",
                                                                                 "Gaussian Splatting scene (.ply, INRIA format)")), m_p.ply);
                if (ply != m_p.ply) SetPly(ply);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var dir = File.Exists(m_p.ply) ? Path.GetDirectoryName(m_p.ply) : "";
                    var sel = EditorUtility.OpenFilePanel(T("Escena 3DGS", "3DGS scene"), dir, "ply");
                    if (!string.IsNullOrEmpty(sel)) SetPly(sel);
                    GUI.FocusControl(null);
                }
            }
            if (!File.Exists(m_p.ply))
            {
                EditorGUILayout.HelpBox(T("Elige el .ply de la escena.", "Pick the scene .ply."), MessageType.Info);
                errors = true;
            }

            m_p.nombre = EditorGUILayout.TextField(new GUIContent(T("Nombre", "Name"),
                T($"Carpeta en {SplatEnvironments.Root} y nombre de las escenas", $"Folder in {SplatEnvironments.Root} and scene name")), m_p.nombre);
            if (!s_validName.IsMatch(m_p.nombre ?? ""))
            {
                EditorGUILayout.HelpBox(T("El nombre solo puede llevar letras sin tilde, números, _ y -.",
                                          "The name may only contain letters, digits, _ and -."), MessageType.Error);
                errors = true;
            }
            else if (Directory.Exists($"{SplatEnvironments.Root}/{m_p.nombre}"))
                EditorGUILayout.HelpBox(T($"Ya existe «{m_p.nombre}»: se actualizarán sus splats y colisiones; sus escenas se mantienen " +
                                          "salvo que elijas recrearlas.",
                                          $"'{m_p.nombre}' already exists: its splats and collisions will be updated; its scenes are " +
                                          "kept unless you choose to recreate them."), MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                m_p.colmap = EditorGUILayout.TextField(new GUIContent("COLMAP (sparse/0)",
                    T("Cámaras del vídeo: dan el suelo, la escala y la cámara inicial. Se busca sola junto al PLY.",
                      "Video cameras: they give the floor, the scale and the initial camera. Found automatically next to the PLY.")), m_p.colmap);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var sel = EditorUtility.OpenFolderPanel(T("Carpeta sparse/0 de COLMAP", "COLMAP sparse/0 folder"), m_p.colmap, "");
                    if (!string.IsNullOrEmpty(sel)) m_p.colmap = sel;
                    GUI.FocusControl(null);
                }
            }
            if (string.IsNullOrEmpty(m_p.colmap))
                EditorGUILayout.HelpBox(T("Sin COLMAP el suelo se busca en los splats y la escala es la del archivo (pon la escala " +
                                          "en Opciones si la sabes).",
                                          "Without COLMAP the floor is searched in the splats and the scale is the file's (set the " +
                                          "scale in Options if you know it)."), MessageType.Warning);
            else if (!File.Exists(Path.Combine(m_p.colmap, "images.txt")))
            {
                EditorGUILayout.HelpBox(File.Exists(Path.Combine(m_p.colmap, "images.bin"))
                    ? T("El modelo COLMAP está en binario: exporta a texto (colmap model_converter --output_type TXT).",
                        "The COLMAP model is binary: export it as text (colmap model_converter --output_type TXT).")
                    : T("No hay images.txt en esa carpeta.", "No images.txt in that folder."), MessageType.Error);
                errors = true;
            }
            else
                m_p.alturaCamara = EditorGUILayout.Slider(new GUIContent(T("Altura de la cámara (m)", "Camera height (m)"),
                    T("A qué altura del suelo ibas grabando: fija la escala real", "How high above the floor you were recording: sets the real scale")),
                    m_p.alturaCamara, 0.5f, 2.5f);

            using (new EditorGUI.DisabledScope(SplatEnvironments.Vr == null))
                m_p.vr = EditorGUILayout.Toggle(new GUIContent(T("Crear también la escena VR", "Also create the VR scene"),
                    SplatEnvironments.Vr == null
                        ? T("Necesita XR Interaction Toolkit y sus Starter Assets", "Needs XR Interaction Toolkit and its Starter Assets")
                        : T("Rig XRI, colisiones de los splats, mesa y objetos agarrables", "XRI rig, splat collisions, table and grabbables")),
                    m_p.vr && SplatEnvironments.Vr != null);

            EditorGUILayout.LabelField(T("Calidades a exportar (todas con LOD)", "Qualities to export (all with LOD)"),
                                       EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                m_p.low = EditorGUILayout.ToggleLeft(new GUIContent("Low detail",
                    T("Sin los splats originales ni SH: visores autónomos y equipos modestos (la más ligera).",
                      "No original splats and no SH: standalone headsets and modest machines (lightest).")), m_p.low);
                m_p.high = EditorGUILayout.ToggleLeft(new GUIContent("High detail",
                    T("Todos los niveles con SH1: PC y PC VR.", "All levels with SH1: PC and PC VR.")), m_p.high);
                m_p.max = EditorGUILayout.ToggleLeft(new GUIContent(T("Original (máxima calidad)", "Original (maximum quality)"),
                    T("Los splats originales con todos sus SH, también con LOD. Ocupa unas 2,5 veces High detail.",
                      "The original splats with all their SH, still with LOD. About 2.5× the size of High detail.")), m_p.max);
            }
            if (!m_p.low && !m_p.high && !m_p.max)
            {
                EditorGUILayout.HelpBox(T("Marca al menos una calidad.", "Tick at least one quality."), MessageType.Error);
                errors = true;
            }

            m_p.antialias = EditorGUILayout.Toggle(new GUIContent(T("Entrenada con antialias", "Trained with antialiasing"),
                T("Mip-Splatting / LichtFeld --enable-mip. Si el nombre del archivo dice «antialias» se detecta solo.",
                  "Mip-Splatting / LichtFeld --enable-mip. Detected automatically if the file name says 'antialias'.")), m_p.antialias);

            m_advanced = EditorGUILayout.Foldout(m_advanced, T("Opciones avanzadas", "Advanced options"), true);
            if (m_advanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    m_p.escala = EditorGUILayout.FloatField(new GUIContent(T("Escala (m/unidad)", "Scale (m/unit)"),
                        T("0 = la estimada con COLMAP (o 1)", "0 = estimated from COLMAP (or 1)")), m_p.escala);
                    m_p.trozo = EditorGUILayout.FloatField(new GUIContent(T("Trozo LOD (m)", "LOD chunk (m)"),
                        T("0 = diagonal/20 entre 1 y 3 m", "0 = diagonal/20 clamped to 1-3 m")), m_p.trozo);
                    m_p.sh = EditorGUILayout.IntSlider(new GUIContent(T("Grado SH", "SH degree"),
                        T("1 basta en VR y ocupa la mitad que 3", "1 is enough for VR and takes half the memory of 3")), m_p.sh, 0, 3);
                    m_p.nivelBajo = EditorGUILayout.IntSlider(new GUIContent(T("Nivel mínimo Low detail", "Low detail min level"),
                        T("Niveles más finos que este no van en Low detail", "Finer levels are left out of Low detail")), m_p.nivelBajo, 0, 3);
                    m_p.agujas = EditorGUILayout.FloatField(new GUIContent(T("Quitar agujas (ratio)", "Remove needles (ratio)"),
                        T("Splats alargados y casi transparentes; 0 = no filtrar", "Elongated near-transparent splats; 0 = off")), m_p.agujas);
                    m_p.vecinos = EditorGUILayout.IntField(new GUIContent(T("Vecinos mínimos", "Min neighbours"),
                        T("Quita niebla y flotantes; 0 = no filtrar", "Removes haze and floaters; 0 = off")), m_p.vecinos);
                    m_p.gigante = EditorGUILayout.FloatField(new GUIContent(T("Quitar gigantes (× diagonal)", "Remove huge (× diagonal)"),
                        T("Splats con σ mayor que esta fracción de la escena", "Splats with σ above this fraction of the scene")), m_p.gigante);
                    if (GUILayout.Button(T("Valores por defecto", "Defaults"), GUILayout.Width(150)))
                        m_p = new SplatImport.Request { ply = m_p.ply, nombre = m_p.nombre, colmap = m_p.colmap, vr = m_p.vr,
                                                        antialias = m_p.antialias, low = m_p.low, high = m_p.high, max = m_p.max };
                }
            }

            var problem = SplatKitPython.Problem();
            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Error);
                m_settings = true;
                errors = true;
            }
            return errors;
        }

        void DoImport()
        {
            try
            {
                EditorPrefs.SetString(KeyRequest, JsonUtility.ToJson(m_p));
                SplatImport.Launch(m_p);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Wami Splat Optimizer", e.Message, "OK");
            }
        }

        void Status(bool running)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(running ? T($"Importando «{SplatImport.Name}»", $"Importing '{SplatImport.Name}'")
                                               : T($"Última importación: «{SplatImport.Name}»", $"Last import: '{SplatImport.Name}'"),
                                       EditorStyles.boldLabel);
            var r = EditorGUILayout.GetControlRect(false, 20);
            var text = SplatImport.Step + (running ? $"  ({SplatImport.Elapsed:h\\:mm\\:ss})" : "");
            EditorGUI.ProgressBar(r, SplatImport.Fraction, text);
            if (running && GUILayout.Button(T("Cancelar", "Cancel")))
                SplatImport.Cancel();

            var log = SplatImport.Log;
            if (log.Length > 12_000) log = "…" + log.Substring(log.Length - 12_000);
            if (string.IsNullOrEmpty(log)) return;
            m_scrollLog = EditorGUILayout.BeginScrollView(m_scrollLog, GUILayout.Height(180));
            var style = new GUIStyle(EditorStyles.label) { wordWrap = false, richText = false, font = EditorStyles.miniFont };
            var h = style.CalcHeight(new GUIContent(log), 10_000);
            EditorGUILayout.SelectableLabel(log, style, GUILayout.Height(h));
            EditorGUILayout.EndScrollView();
            if (running) m_scrollLog.y = float.MaxValue;
        }

        [Serializable] class Summary { public int splats_total; public float escala_aplicada; }

        string Info(string folder)
        {
            m_info ??= new Dictionary<string, string>();
            if (m_info.TryGetValue(folder, out var s)) return s;
            try
            {
                var r = JsonUtility.FromJson<Summary>(File.ReadAllText(SplatEnvironments.ManifestPath(folder)));
                var tiers = string.Join(" · ", SplatEnvironments.Desktop.Where(v => v.Present(folder) != null).Select(v => v.label));
                long bytes = Directory.GetFiles(folder, "*.ply").Sum(f => new FileInfo(f).Length);
                s = T($"{r.splats_total / 1e6:0.0} M splats en la pirámide · {bytes / (1024f * 1024f * 1024f):0.00} GB",
                      $"{r.splats_total / 1e6:0.0} M splats in the pyramid · {bytes / (1024f * 1024f * 1024f):0.00} GB");
                if (r.escala_aplicada > 0 && Math.Abs(r.escala_aplicada - 1) > 1e-4) s += T($" · escala {r.escala_aplicada:0.###} m/u", $" · scale {r.escala_aplicada:0.###} m/u");
                if (!File.Exists(folder + "/colision.json")) s += T(" · sin colisiones", " · no collisions");
                s += "\n" + tiers;
            }
            catch (Exception e)
            {
                s = T("manifiesto ilegible: ", "unreadable manifest: ") + e.Message;
            }
            return m_info[folder] = s;
        }

        void EnvironmentList()
        {
            EditorGUILayout.LabelField(T("Entornos del proyecto", "Project environments"), EditorStyles.boldLabel);
            var folders = SplatEnvironments.Folders();
            if (folders.Length == 0)
            {
                EditorGUILayout.LabelField(T($"Ninguno todavía en {SplatEnvironments.Root}.", $"None yet in {SplatEnvironments.Root}."), EditorStyles.miniLabel);
                return;
            }
            foreach (var c in folders)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(Path.GetFileName(c), EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(Info(c), EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent(T("Escritorio", "Desktop"), SplatEnvironments.ScenePath(c))))
                            OpenOrCreate(SplatEnvironments.ScenePath(c), () => SplatEnvironments.CreateDesktopScene(c));
                        var vr = SplatEnvironments.Vr;
                        using (new EditorGUI.DisabledScope(vr == null || !File.Exists(c + "/colision.json")))
                            if (GUILayout.Button(new GUIContent("VR", vr?.ScenePath(c) ?? T("Necesita XR Interaction Toolkit", "Needs XR Interaction Toolkit"))))
                                OpenOrCreate(vr.ScenePath(c), () => vr.Create(c));
                        if (GUILayout.Button(new GUIContent(T("Recrear escenas", "Recreate scenes"))) &&
                            EditorUtility.DisplayDialog(T("Recrear escenas", "Recreate scenes"),
                                T($"¿Recrear las escenas de «{Path.GetFileName(c)}»? Se pierde lo que les hayas añadido a mano.",
                                  $"Recreate the scenes of '{Path.GetFileName(c)}'? Anything added by hand is lost."),
                                T("Recrear", "Recreate"), T("Cancelar", "Cancel")))
                            Run(() => SplatImport.CreateScenes(c, true, true));
                        if (GUILayout.Button(T("Carpeta", "Folder"), GUILayout.Width(64)))
                            EditorUtility.RevealInFinder(c);
                    }
                }
            }
        }

        static void OpenOrCreate(string scene, Action create) => Run(() =>
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (File.Exists(scene)) EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            else create();
        });

        /// <summary>Fuera de OnGUI (abrir o crear escenas dentro de él rompe el layout).</summary>
        static void Run(Action a)
        {
            EditorApplication.delayCall += () =>
            {
                try { a(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Wami Splat Optimizer", e.Message, "OK");
                }
            };
        }
    }
}
