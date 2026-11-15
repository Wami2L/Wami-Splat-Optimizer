// SplatKit — GPU tests of the sort keys and the profile file.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using Gsplat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplatKit.Tests
{
    /// <summary>
    /// La ordenación de SplatKit (claves radiales o por la vista, de 16 o 32 bits) deja los splats de atrás hacia
    /// delante, con el error que permite la cuantización. Necesita GPU con compute (D3D12/Vulkan).
    /// SplatKit sorting (radial or view keys, 16 or 32 bits) leaves splats back to front.
    /// </summary>
    public class SortKeyTests
    {
        const int N = 5000;

        static byte[] Ply(Vector3[] pos)
        {
            var props = new[] { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2",
                                "rot_0", "rot_1", "rot_2", "rot_3" };
            using var ms = new MemoryStream();
            var header = "ply\nformat binary_little_endian 1.0\nelement vertex " + pos.Length + "\n" +
                         string.Concat(props.Select(p => "property float " + p + "\n")) + "end_header\n";
            var hb = System.Text.Encoding.ASCII.GetBytes(header);
            ms.Write(hb, 0, hb.Length);
            using var w = new BinaryWriter(ms);
            foreach (var p in pos)
            {
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
                w.Write(0.5f); w.Write(0.5f); w.Write(0.5f);
                w.Write(2f);                                     // logit(alfa) ≈ 0,88
                w.Write(-4f); w.Write(-4f); w.Write(-4f);        // log(σ) ≈ 1,8 cm
                w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
            }
            w.Flush();
            return ms.ToArray();
        }

        static float Half(float v) => Mathf.HalfToFloat(Mathf.FloatToHalf(v));

        [TestCase(GsplatRenderer.SortKeyMode.Radial, 16)]
        [TestCase(GsplatRenderer.SortKeyMode.Radial, 32)]
        [TestCase(GsplatRenderer.SortKeyMode.View, 16)]
        [TestCase(GsplatRenderer.SortKeyMode.View, 32)]
        public void OrdenaDeAtrasHaciaDelante(GsplatRenderer.SortKeyMode mode, int bits)
        {
            var settingsValid = GsplatSettings.Instance.Valid;  // cargar los ajustes inicia el sorter / loads the sorter
            if (!SystemInfo.supportsComputeShaders || !settingsValid || !GsplatSorter.Instance.Valid)
                Assert.Ignore("Sin compute / no compute support");
            var rng = new System.Random(1);
            float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var pos = Enumerable.Range(0, N).Select(_ => new Vector3(R(-6, 6), R(-3, 3), R(1, 30))).ToArray();

            var asset = ScriptableObject.CreateInstance<GsplatAssetSpark>();
            var camGo = new GameObject("SortTestCamera");
            var go = new GameObject("SortTestSplats");
            try
            {
                asset.LoadFromPlyBytes(Ply(pos), null, SourceCoordinates.RUF, 0f);
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.transform.SetPositionAndRotation(new Vector3(0.3f, 0.2f, -0.5f), Quaternion.Euler(4f, 7f, 0f));
                var gs = go.AddComponent<GsplatRenderer>();
                gs.ExternalTick = true;
                gs.GsplatAsset = asset;
                gs.SortMode = GsplatRenderer.GsplatSortMode.OnCameraMove;
                gs.SortKey = mode;
                gs.SortKeyBits = bits;
                gs.Tick();                                       // sube los datos / upload
                Assert.IsTrue(GsplatSorter.Instance.GatherGsplatsForCamera(cam));
                var count = (int)gs.RemainingCount;
                Assert.AreEqual(N, count);

                var cmd = new CommandBuffer { name = "SplatKitSortTest" };
                GsplatSorter.Instance.DispatchSort(cmd, cam);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();
                var order = new uint[count];
                gs.SorterResource.OrderBuffer.GetData(order, 0, 0, count);

                Assert.AreEqual(N, order.Distinct().Count(), "el orden es una permutación / order is a permutation");
                var view = cam.worldToCameraMatrix;
                float Key(uint i)
                {
                    var p = new Vector3(Half(pos[i].x), Half(pos[i].y), Half(pos[i].z));
                    var v = view.MultiplyPoint3x4(p);
                    return mode == GsplatRenderer.SortKeyMode.Radial ? v.magnitude : -v.z;
                }
                // Rango de cuantización: la distancia máxima de la caja (claves de 16 bits) / quantization step.
                var tol = bits == 16 ? 45f / 65535f * 2f : 1e-4f;
                var bad = 0;
                for (int k = 1; k < count; k++)
                    if (Key(order[k]) > Key(order[k - 1]) + tol) bad++;
                Assert.AreEqual(0, bad, "splats fuera de orden / out-of-order splats");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void PerfilesBilingues()
        {
            var set = SplatProfileSet.Builtin;
            Assert.Greater(set.profiles.Length, 4);
            Assert.IsNotNull(set.Get(set.@default));
            foreach (var p in set.profiles)
            {
                Assert.IsFalse(string.IsNullOrEmpty(p.label.es), p.id);
                Assert.IsFalse(string.IsNullOrEmpty(p.label.en), p.id);
                Assert.IsNotNull(p.runtime, p.id);
            }
            Assert.AreEqual(150000, set.Get("quest2").runtime.splat_budget);
            Assert.IsTrue(set.Get("auto").auto);
            try
            {
                SplatLang.Override = "en";
                Assert.AreEqual("Quest 2 standalone", set.Get("quest2").Label);
                SplatLang.Override = "es";
                Assert.AreEqual("Quest 2 autónomo", set.Get("quest2").Label);
            }
            finally
            {
                SplatLang.Override = null;
            }
        }
    }
}
