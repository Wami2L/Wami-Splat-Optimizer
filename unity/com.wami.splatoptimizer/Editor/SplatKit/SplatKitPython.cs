// SplatKit — where the offline pipeline (python -m splatkit) lives and which Python runs it.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Dónde está la cadena offline de SplatKit (`python -m splatkit`) y con qué Python se ejecuta. Por defecto, la
    /// carpeta `python/` del mismo repo que este paquete (unity/com.wami.splatoptimizer → ../../python). Se guarda en EditorPrefs
    /// (por equipo, no va en el proyecto).
    ///
    /// Where SplatKit's offline pipeline lives and which Python runs it (EditorPrefs, per machine).
    /// </summary>
    public static class SplatKitPython
    {
        const string KeyDir = "SplatKit.PythonDir", KeyPython = "SplatKit.Python", KeyWorkspace = "SplatKit.Workspace";

        static string PackageRoot
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SplatKitPython).Assembly);
                return info != null ? info.resolvedPath : "";
            }
        }

        /// <summary>Carpeta que contiene el paquete python `splatkit/`. / Folder holding the `splatkit/` package.</summary>
        public static string PythonDir
        {
            get
            {
                var v = EditorPrefs.GetString(KeyDir, "");
                if (v != "") return v;
                var root = PackageRoot;
                if (root == "") return "";
                var cand = Path.GetFullPath(Path.Combine(root, "..", "..", "python"));
                return Directory.Exists(Path.Combine(cand, "splatkit")) ? cand : "";
            }
            set => EditorPrefs.SetString(KeyDir, value ?? "");
        }

        public static string Python
        {
            get
            {
                var v = EditorPrefs.GetString(KeyPython, "");
                return v != "" ? v : PythonDefault();
            }
            set => EditorPrefs.SetString(KeyPython, value == PythonDefault() ? "" : value ?? "");
        }

        /// <summary>El .venv junto a python/ si existe; si no, el python del PATH. / The .venv if present, else PATH.</summary>
        public static string PythonDefault()
        {
            var dir = PythonDir;
            var venv = Path.Combine(dir, ".venv", Application.platform == RuntimePlatform.WindowsEditor ? "Scripts" : "bin",
                                    Application.platform == RuntimePlatform.WindowsEditor ? "python.exe" : "python");
            return dir != "" && File.Exists(venv) ? venv : "python";
        }

        /// <summary>Carpeta de trabajo de las escenas (intermedios, análisis). / Scene workspace.</summary>
        public static string Workspace
        {
            get
            {
                var v = EditorPrefs.GetString(KeyWorkspace, "");
                if (v != "") return v;
                var dir = PythonDir;
                return dir != "" ? Path.GetFullPath(Path.Combine(dir, "..", "escenas")) : "";
            }
            set => EditorPrefs.SetString(KeyWorkspace, value ?? "");
        }

        /// <summary>Primeros argumentos: `-u -m splatkit --espacio &lt;ws&gt;`. / Leading arguments.</summary>
        public static List<string> CliArgs(Func<string, string> quote) =>
            new List<string> { "-u", "-m", "splatkit", "--espacio", quote(Workspace) };

        /// <summary>Qué impide ejecutar la cadena, o null. / What prevents running the pipeline, or null.</summary>
        public static string Problem()
        {
            if (PythonDir == "" || !File.Exists(Path.Combine(PythonDir, "splatkit", "cli.py")))
                return SplatLang.T("No encuentro la carpeta python/ de SplatKit: indícala en los ajustes.",
                                   "SplatKit's python/ folder was not found: set it in the settings.");
            if (Python.IndexOfAny(new[] { '/', '\\' }) >= 0 && !File.Exists(Python))
                return SplatLang.T($"No existe el Python «{Python}».", $"Python '{Python}' does not exist.");
            return null;
        }

        static string Folder(string label, string tooltip, string value, bool file)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(new GUIContent(label, tooltip), value);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var sel = file ? EditorUtility.OpenFilePanel(label, Path.GetDirectoryName(value) ?? "", "exe")
                                   : EditorUtility.OpenFolderPanel(label, value, "");
                    if (!string.IsNullOrEmpty(sel)) value = sel;
                    GUI.FocusControl(null);
                }
            }
            return value;
        }

        /// <summary>Ajustes en IMGUI (para ventanas de importación). / IMGUI settings block.</summary>
        public static void SettingsGUI()
        {
            using (new EditorGUI.IndentLevelScope())
            {
                var dir = Folder(SplatLang.T("SplatKit python/", "SplatKit python/"),
                                 SplatLang.T("Carpeta con el paquete splatkit/ (repo SplatKit)", "Folder with the splatkit/ package"),
                                 PythonDir, false);
                if (dir != PythonDir) PythonDir = dir;
                var py = Folder("Python", SplatLang.T("Con numpy, scipy y Pillow. Por defecto, python/.venv o el del PATH",
                                                      "With numpy, scipy and Pillow. Default: python/.venv or PATH"), Python, true);
                if (py != Python) Python = py;
                var ws = Folder(SplatLang.T("Espacio de trabajo", "Workspace"),
                                SplatLang.T("Donde se guardan los intermedios de cada escena", "Where each scene's intermediates go"),
                                Workspace, false);
                if (ws != Workspace) Workspace = ws;
                EditorGUILayout.LabelField(SplatLang.T("Se guardan en este equipo (EditorPrefs), no en el proyecto.",
                                                       "Stored on this machine (EditorPrefs), not in the project."),
                                           EditorStyles.miniLabel);
            }
        }
    }
}
