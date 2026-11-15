// Wami Splat Optimizer — per-project folders for imported environments (ProjectSettings/WamiSplatOptimizer.json).
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Dónde deja el addon los entornos importados, sus escenas, materiales y capturas. Se guarda en
    /// ProjectSettings/WamiSplatOptimizer.json (va con el proyecto). / Where imported environments, scenes, materials
    /// and captures go (stored in ProjectSettings/WamiSplatOptimizer.json).
    /// </summary>
    [Serializable]
    public class SplatProjectSettings
    {
        public string environments = "Assets/WamiSplats/Environments";
        public string scenes = "Assets/WamiSplats/Scenes";
        public string scenePrefix = "Splat_";
        public string materials = "Assets/WamiSplats/Materials";
        public string captures = "WamiSplatCaptures";

        const string Path_ = "ProjectSettings/WamiSplatOptimizer.json";
        static SplatProjectSettings s_instance;

        public static SplatProjectSettings Instance
        {
            get
            {
                if (s_instance != null) return s_instance;
                s_instance = new SplatProjectSettings();
                if (File.Exists(Path_)) JsonUtility.FromJsonOverwrite(File.ReadAllText(Path_), s_instance);
                return s_instance;
            }
        }

        public void Save() => File.WriteAllText(Path_, JsonUtility.ToJson(this, true));

        static string Field(string label, string tooltip, string value) =>
            EditorGUILayout.TextField(new GUIContent(label, tooltip), value);

        [SettingsProvider]
        static SettingsProvider Provider() => new SettingsProvider("Project/Wami Splat Optimizer/Import", SettingsScope.Project)
        {
            label = "Import",
            guiHandler = _ => OnGUI(),
        };

        /// <summary>Bloque IMGUI de los ajustes del proyecto. / IMGUI block of the project settings.</summary>
        public static void OnGUI()
        {
            var s = Instance;
            EditorGUI.BeginChangeCheck();
            s.environments = Field(SplatLang.T("Carpeta de entornos", "Environments folder"),
                                   SplatLang.T("Una subcarpeta por escena importada", "One subfolder per imported scene"), s.environments);
            s.scenes = Field(SplatLang.T("Carpeta de escenas", "Scenes folder"), "", s.scenes);
            s.scenePrefix = Field(SplatLang.T("Prefijo de escenas", "Scene prefix"), "<prefijo><nombre>.unity / <prefix><name>.unity", s.scenePrefix);
            s.materials = Field(SplatLang.T("Carpeta de materiales", "Materials folder"), "", s.materials);
            s.captures = Field(SplatLang.T("Carpeta de capturas", "Captures folder"),
                               SplatLang.T("Relativa al proyecto (fuera de Assets)", "Relative to the project (outside Assets)"), s.captures);
            if (EditorGUI.EndChangeCheck()) s.Save();
        }
    }
}
