// Wami Splat Optimizer — several versions of the same splat environment, one active at a time.
// SPDX-License-Identifier: MIT

using System;
using Gsplat;
using UnityEngine;
using UnityEngine.Serialization;
#if SPLATKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SplatKit
{
    /// <summary>
    /// Varias versiones del mismo entorno Gaussian Splatting (p. ej. la de PC y la ligera de Quest): solo una activa;
    /// las teclas 1-9 cambian de una a otra en Play. Muestra la activa y los créditos del autor (las escenas con licencia
    /// CC BY exigen citarlo).
    ///
    /// Several versions of the same splat environment (e.g. PC and light Quest pyramids): only one is active; keys 1-9
    /// switch in Play mode. Shows the active one and the author credits.
    /// </summary>
    [AddComponentMenu("Wami Splat Optimizer/Splat Variants")]
    public class SplatVariants : MonoBehaviour
    {
        [Serializable]
        public class Variant
        {
            [FormerlySerializedAs("nombre")] public string name;
            [FormerlySerializedAs("objeto")] public GameObject target;
        }

        [FormerlySerializedAs("variantes")] public Variant[] variants = Array.Empty<Variant>();
        [FormerlySerializedAs("activa")] public int active;
        [FormerlySerializedAs("creditos"), TextArea] public string credits;
        [FormerlySerializedAs("mostrarRotulo")] public bool showLabel = true;
        [Tooltip("Teclas 1-9 para cambiar de variante / keys 1-9 switch variant")]
        [FormerlySerializedAs("teclas")] public bool keys = true;

        void OnEnable() => Activate(active);

        public void Activate(int index)
        {
            if (variants.Length == 0) return;
            active = Mathf.Clamp(index, 0, variants.Length - 1);
            for (int i = 0; i < variants.Length; i++)
                if (variants[i].target) variants[i].target.SetActive(i == active);
        }

        /// <summary>Índice de la primera variante cuyo nombre contiene el texto, o -1. / First variant matching.</summary>
        public int Find(string text) => Array.FindIndex(variants, v => v.name != null && v.name.Contains(text));

        void Update()
        {
#if SPLATKIT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null || !keys) return;
            for (int i = 0; i < Mathf.Min(9, variants.Length); i++)
                if (kb[Key.Digit1 + i].wasPressedThisFrame) Activate(i);
#endif
        }

        void OnGUI()
        {
            if (!showLabel || variants.Length == 0) return;
            var v = variants[active];
            var gs = v.target ? v.target.GetComponent<GsplatRenderer>() : null;
            var lod = v.target ? v.target.GetComponent<SplatLod>() : null;
            var total = gs && gs.GsplatAsset ? gs.GsplatAsset.SplatCount : 0;
            var n = lod
                ? SplatLang.T($" · dibujando {lod.SelectedSplats:N0} de {total:N0} splats ({lod.VisibleChunks} trozos, {lod.EffectiveAngleDeg:F2}°)",
                              $" · drawing {lod.SelectedSplats:N0} of {total:N0} splats ({lod.VisibleChunks} chunks, {lod.EffectiveAngleDeg:F2}°)")
                : total > 0 ? $" · {total:N0} splats" : "";
            var help = SplatLang.T($"1-{Mathf.Min(9, variants.Length)}: cambiar · botón derecho + ratón: mirar · WASD/QE: mover · Shift: rápido",
                                   $"1-{Mathf.Min(9, variants.Length)}: switch · right mouse + move: look · WASD/QE: move · Shift: fast");
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };
            GUI.Box(new Rect(10, 10, 820, 46), $"[{active + 1}/{variants.Length}] {v.name}{n}\n{help}", style);
            if (!string.IsNullOrEmpty(credits))
                GUI.Box(new Rect(10, Screen.height - 50, 820, 40), credits, style);
        }
    }
}
