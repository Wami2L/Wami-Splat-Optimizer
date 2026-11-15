// SplatKit — quality profiles shared with the python pipeline (Resources/SplatKitProfiles.json).
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace SplatKit
{
    /// <summary>Idioma de los textos de SplatKit / language of SplatKit texts.</summary>
    public static class SplatLang
    {
        /// <summary>null = automático (idioma del sistema) / null = automatic (system language).</summary>
        public static string Override;

        public static bool Spanish => Override != null ? Override == "es" : Application.systemLanguage == SystemLanguage.Spanish;

        public static string T(string es, string en) => Spanish ? es : en;

        public static string Pick(Bilingual b) => b == null ? "" : Spanish ? b.es : b.en;
    }

    [Serializable]
    public class Bilingual
    {
        public string es, en;
        public override string ToString() => SplatLang.Pick(this);
    }

    /// <summary>Parte de ejecución de un perfil (mismas claves que profiles.json). / Runtime part of a profile.</summary>
    [Serializable]
    public class SplatRuntimeSettings
    {
        public int splat_budget = 600000;
        public float lod_angle_deg = 0.15f;
        public int sh_degree = 1;
        public float splat_resolution = 1f;
        public bool depth_prepass = true;
        public string sort_mode = "radial";
        public int sort_key_bits = 16;
        public bool stable_xr_lod = true;
        public float behind_penalty = 1.5f;
        public float min_pixel_size = 2f;
        public float min_contribution = 2f;
        public float periphery = 1f;
        public int target_hz = 90;
        public bool adaptive = true;

        public SplatRuntimeSettings Clone() => (SplatRuntimeSettings)MemberwiseClone();
    }

    [Serializable]
    public class SplatBake
    {
        public string pyramid = "full";
    }

    [Serializable]
    public class SplatProfile
    {
        public string id;
        public Bilingual label, description;
        public bool auto;
        public SplatBake bake;
        public SplatRuntimeSettings runtime;

        public string Label => SplatLang.Pick(label);
        public string Description => SplatLang.Pick(description);
    }

    [Serializable]
    public class SplatPyramid
    {
        public string id, file;
        public int min_level, sh;
        public Bilingual label;
    }

    [Serializable]
    public class SplatProfileSet
    {
        public int version;
        public string @default = "auto";
        public SplatPyramid[] pyramids;
        public SplatProfile[] profiles;

        static SplatProfileSet s_builtin;

        /// <summary>Los perfiles del paquete / the package profiles.</summary>
        public static SplatProfileSet Builtin
        {
            get
            {
                if (s_builtin != null) return s_builtin;
                var ta = Resources.Load<TextAsset>("SplatKitProfiles");
                s_builtin = ta ? FromJson(ta.text) : new SplatProfileSet { profiles = Array.Empty<SplatProfile>() };
                return s_builtin;
            }
        }

        // El campo @default se llama "default" para JsonUtility, igual que la clave del JSON.
        public static SplatProfileSet FromJson(string json) => JsonUtility.FromJson<SplatProfileSet>(json);

        public SplatProfile Get(string id)
        {
            foreach (var p in profiles)
                if (p.id == id) return p;
            return null;
        }

        public string[] Ids
        {
            get
            {
                var r = new string[profiles.Length];
                for (int i = 0; i < r.Length; i++) r[i] = profiles[i].id;
                return r;
            }
        }

        /// <summary>
        /// Perfil para este dispositivo (lo que hace el perfil «auto»). / Profile for this device ("auto").
        /// Android (visor autónomo): quest3 si la GPU es Adreno 7xx, si no quest2; con XR en PC: pcvr_high con ≥ 8 GB de
        /// VRAM, si no pcvr_low; sin XR: desktop.
        /// </summary>
        public SplatProfile ForThisDevice(bool xrActive)
        {
            string id;
            if (Application.platform == RuntimePlatform.Android)
                id = SystemInfo.graphicsDeviceName.Contains("Adreno (TM) 7") ? "quest3" : "quest2";
            else if (xrActive)
                id = SystemInfo.graphicsMemorySize >= 8000 ? "pcvr_high" : "pcvr_low";
            else
                id = "desktop";
            return Get(id) ?? Get("pcvr_low") ?? profiles[0];
        }
    }
}
