// Wami Splat Optimizer — imported splat environments: variants, desktop scene, captures and batch helpers.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Entornos Gaussian Splatting importados. Cada uno es una carpeta de <see cref="SplatProjectSettings.environments"/>
    /// que deja la ventana de importación (o `python -m splatkit preparar ... --unity`): pirámides LOD de PC y de Quest
    /// (PLY + manifiesto), colision.json y LICENCIA.txt. Aquí se crean sus escenas:
    ///   &lt;prefijo&gt;&lt;nombre&gt;.unity     escritorio: cámara de vuelo, tecla 1 = PC, 2 = Quest
    ///   &lt;prefijo&gt;&lt;nombre&gt;_VR.unity  VR con colisiones (SplatVrScene, si XR Interaction Toolkit está instalado)
    ///
    /// Imported Gaussian Splatting environments and their scenes.
    /// </summary>
    public static class SplatEnvironments
    {
        public static string Root => SplatProjectSettings.Instance.environments.TrimEnd('/');

        public class Variant
        {
            public string file, altFile, label;
            public int budget;
            public string profile;          // perfil de calidad (SplatQuality) / quality profile

            /// <summary>El archivo presente en la carpeta (file o, si no, altFile), o null.</summary>
            public string Present(string folder) =>
                File.Exists(Path.Combine(folder, file)) ? file
                : altFile != null && File.Exists(Path.Combine(folder, altFile)) ? altFile : null;
        }

        // Calidades que exporta `splatkit preparar --piramides low,high,max`, todas con LOD.
        public const string HighLabel = "High detail", LowLabel = "Low detail", MaxLabel = "Original (max)";
        static readonly string[] s_manifests = { "piramide.json", "piramide_low.json", "piramide_max.json", "piramide_quest.json" };

        /// <summary>Escritorio: High detail, Low detail y Original si están. / Desktop variants.</summary>
        public static readonly Variant[] Desktop =
        {
            new Variant { file = "piramide.ply", label = HighLabel, budget = 1_000_000, profile = "desktop" },
            new Variant { file = "piramide_low.ply", altFile = "piramide_quest.ply", label = LowLabel, budget = 150_000, profile = "quest2" },
            new Variant { file = "piramide_max.ply", label = MaxLabel, budget = 2_500_000, profile = "ultra" },
        };

        /// <summary>
        /// Escena VR (la registra el ensamblado opcional SplatKit.Editor.XR si XR Interaction Toolkit está instalado).
        /// VR scene factory, registered by the optional XR assembly.
        /// </summary>
        public interface IVrScenes
        {
            string ScenePath(string folder);
            Scene Create(string folder, string path = null, bool props = true);
        }

        public static IVrScenes Vr;

        [Serializable] public class Camara { public float[] pos; public float[] adelante; }
        [Serializable] public class Manifest
        {
            public string nombre;
            public float[] rotacion_unity;
            public Camara camara_inicio;
            public bool antialias;          // entrenada con antialias (Mip-Splatting)
        }

        /// <summary>Carpetas de entorno (las que tienen alguna pirámide). / Environment folders.</summary>
        public static string[] Folders() =>
            Directory.Exists(Root)
                ? Directory.GetDirectories(Root).Select(d => d.Replace('\\', '/'))
                           .Where(d => ManifestPath(d) != null).OrderBy(d => d).ToArray()
                : Array.Empty<string>();

        /// <summary>El manifiesto de la primera pirámide presente, o null. / First pyramid manifest present.</summary>
        public static string ManifestPath(string folder) =>
            s_manifests.Select(m => folder + "/" + m).FirstOrDefault(File.Exists);

        public static string ScenePath(string folder, string suffix = "")
        {
            var s = SplatProjectSettings.Instance;
            return $"{s.scenes.TrimEnd('/')}/{s.scenePrefix}{Path.GetFileName(folder)}{suffix}.unity";
        }

        public static Manifest ReadManifest(string folder) =>
            JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath(folder) ?? folder + "/piramide.json"));

        /// <summary>Se asegura de que el PLY se importe en RDF. / Make sure the PLY is imported as RDF.</summary>
        public static void EnsureRdf(string path)
        {
            if (AssetImporter.GetAtPath(path) is Gsplat.Editor.GsplatImporter imp &&
                imp.SourceCoordinates != SourceCoordinates.RDF)
            {
                imp.SourceCoordinates = SourceCoordinates.RDF;
                imp.SaveAndReimport();
            }
        }

        /// <summary>Créditos a partir de LICENCIA.txt (Title/Author/License). / Credits from LICENCIA.txt.</summary>
        public static string Credits(string folder)
        {
            var name = Path.GetFileName(folder);
            var lic = folder + "/LICENCIA.txt";
            if (!File.Exists(lic)) return SplatLang.T($"«{name}»: escaneo propio.", $"'{name}': own scan.");
            var fields = File.ReadAllLines(lic).Select(l => l.Split(new[] { ':' }, 2)).Where(p => p.Length == 2)
                             .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
            string Get(string k) => fields.TryGetValue(k, out var v) ? v : null;
            var text = string.Join(", ", new[] { Get("Title") is string t ? $"«{t}»" : null, Get("Author"), Get("License") }
                                         .Where(x => !string.IsNullOrEmpty(x)));
            return string.IsNullOrEmpty(text) ? File.ReadAllText(lic).Trim() : text;
        }

        /// <summary>
        /// Crea en la escena abierta la raíz del entorno (con la rotación de nivelación del manifiesto) y una variante
        /// por pirámide presente: GsplatRenderer + SplatLod + SplatQuality. La primera queda activa.
        /// Creates the environment root and one variant per pyramid present.
        /// </summary>
        public static SplatVariants CreateVariants(string folder, Variant[] variants)
        {
            var present = variants.Where(v => v.Present(folder) != null).ToArray();
            if (present.Length == 0)
                throw new FileNotFoundException(SplatLang.T($"No hay pirámides en {folder}", $"No pyramids in {folder}"));
            var man = ReadManifest(folder);
            var root = new GameObject($"{Path.GetFileName(folder)} (Gaussian Splatting)");
            var r = man.rotacion_unity;
            if (r != null && r.Length == 4) root.transform.rotation = new Quaternion(r[0], r[1], r[2], r[3]);
            var sel = root.AddComponent<SplatVariants>();
            sel.credits = Credits(folder);
            sel.variants = present.Select(v =>
            {
                var path = $"{folder}/{v.Present(folder)}";
                EnsureRdf(path);
                var go = new GameObject(v.label);
                go.transform.SetParent(root.transform, false);
                var gs = go.AddComponent<GsplatRenderer>();
                gs.GsplatAsset = AssetDatabase.LoadAssetAtPath<GsplatAsset>(path);
                gs.SHDegree = 3;
                gs.GammaToLinear = PlayerSettings.colorSpace == ColorSpace.Linear;   // splats entrenados en gamma
                gs.AsyncUpload = false;         // con subida asíncrona la selección apuntaría a datos aún no subidos
                gs.DepthPrepass = true;         // oclusión por early-Z
                gs.AntiAlias = man.antialias;
                var lod = go.AddComponent<SplatLod>();
                lod.manifest = AssetDatabase.LoadAssetAtPath<TextAsset>(Path.ChangeExtension(path, ".json"));
                lod.budget = v.budget;
                if (!string.IsNullOrEmpty(v.profile))
                    go.AddComponent<SplatQuality>().SetProfile(v.profile);   // aplica ya el perfil (no el de por defecto)
                return new SplatVariants.Variant { name = v.label, target = go };
            }).ToArray();
            sel.Activate(0);
            return sel;
        }

        /// <summary>Pose inicial (mundo): la cámara central del recorrido, o el origen a 1,6 m. / Initial pose.</summary>
        public static (Vector3 pos, Vector3 forward) InitialPose(string folder)
        {
            var c = ReadManifest(folder).camara_inicio;
            if (c?.pos?.Length == 3 && c.adelante?.Length == 3)
            {
                var f = new Vector3(c.adelante[0], 0, c.adelante[2]);
                return (new Vector3(c.pos[0], c.pos[1], c.pos[2]), f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward);
            }
            return (new Vector3(0, 1.6f, 0), Vector3.forward);
        }

        /// <summary>Escena de escritorio: variantes + cámara de vuelo en la pose inicial. / Desktop scene.</summary>
        public static Scene CreateDesktopScene(string folder, string path = null)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = CreateVariants(folder, Desktop);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 70f;
            var (pos, fwd) = InitialPose(folder);
            camGo.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd));
            camGo.AddComponent<FlyCamera>();

            path ??= ScenePath(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            SceneView.lastActiveSceneView?.AlignViewToObject(camGo.transform);
            Debug.Log($"[Wami Splat Optimizer] {path}: {root.variants.Length} variante(s)/variant(s), cámara/camera {pos}");
            return scene;
        }

        public static string CapturesFolder
        {
            get
            {
                var d = Path.Combine(Path.GetDirectoryName(Application.dataPath), SplatProjectSettings.Instance.captures);
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>Renderiza la cámara con cada variante a &lt;capturas&gt;/&lt;prefijo&gt;_N.png (por ticks).</summary>
        public static IEnumerator<object> CaptureVariants(SplatVariants root, Camera cam, string prefix)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            var prevTarget = cam.targetTexture;
            cam.targetTexture = rt;
            for (int i = 0; i < root.variants.Length; i++)
            {
                root.Activate(i);
                var gs = root.variants[i].target.GetComponent<GsplatRenderer>();
                gs.Tick();                          // sube los datos (fuera de Play no hay player loop)
                yield return null;
                var lod = gs.GetComponent<SplatLod>();
                if (lod)
                {
                    lod.targetCamera = cam;
                    lod.Refresh(cam, true);
                    Debug.Log($"[Wami Splat Optimizer] {root.variants[i].name}: LOD {lod.SelectedSplats:N0} splats, " +
                              $"{lod.VisibleChunks} trozos/chunks, {lod.EffectiveAngleDeg:F2}°");
                }
                for (int f = 0; f < 3; f++)         // un par de frames para que la ordenación se estabilice
                {
                    GsplatSorter.Instance.Update();
                    gs.Tick();
                    cam.Render();
                    if (f < 2) yield return null;
                }
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(CapturesFolder, $"{prefix}_{i + 1}.png"), tex.EncodeToPNG());
                yield return null;
            }
            cam.targetTexture = prevTarget;
            UnityEngine.Object.DestroyImmediate(rt);
        }

        /// <summary>Ejecuta pasos, uno por tick del editor, y sale de Unity al acabar (batch). / Batch runner.</summary>
        public static void RunBatch(IEnumerator<object> steps)
        {
            void Tick()
            {
                try
                {
                    if (steps.MoveNext()) return;
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
            EditorApplication.update += Tick;
        }

        /// <summary>-entorno &lt;nombre&gt; (o -environment) de la línea de órdenes. / Command-line environment.</summary>
        public static string ArgEnvironment()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(args, a => a == "-entorno" || a == "-environment");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>
        /// -batchmode -executeMethod SplatKit.EditorTools.SplatEnvironments.BatchCreateAndCapture [-entorno X] (sin -quit):
        /// crea las escenas de escritorio y captura su cámara inicial. / Creates desktop scenes and captures them.
        /// </summary>
        public static void BatchCreateAndCapture() => RunBatch(Steps());

        static IEnumerator<object> Steps()
        {
            var only = ArgEnvironment();
            var folders = Folders().Where(c => only == null || Path.GetFileName(c) == only).ToArray();
            if (folders.Length == 0)
                throw new FileNotFoundException(SplatLang.T($"No hay entornos en {Root}", $"No environments in {Root}"));
            foreach (var c in folders)
            {
                CreateDesktopScene(c);
                yield return null;
                var root = UnityEngine.Object.FindAnyObjectByType<SplatVariants>();
                var it = CaptureVariants(root, Camera.main, "entorno_" + Path.GetFileName(c));
                while (it.MoveNext()) yield return it.Current;
            }
        }
    }
}
