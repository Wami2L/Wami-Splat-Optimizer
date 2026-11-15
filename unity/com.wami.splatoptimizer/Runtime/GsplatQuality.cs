// SplatKit — global quality parameters of the splat shaders.
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    /// <summary>
    /// SplatKit: parámetros globales de los shaders de splats (los fija SplatKit.SplatQuality según el perfil).
    /// Global parameters of the splat shaders (set by SplatKit.SplatQuality from the active profile).
    /// </summary>
    public static class GsplatQuality
    {
        /// <summary>Splats whose both axes are shorter than this (pixels) are culled. Original gsplat: 2.</summary>
        public static float MinPixelSize = 2f;

        /// <summary>Splats with alpha × pixel area below this are culled (PlayCanvas minContribution). 0 = off.</summary>
        public static float MinContribution;

        static readonly int k_params = Shader.PropertyToID("_SplatKitParams");

        public static void PushGlobals() =>
            Shader.SetGlobalVector(k_params, new Vector4(Mathf.Max(MinPixelSize, 0f), Mathf.Max(MinContribution, 0f), 0f, 1f));
    }
}
