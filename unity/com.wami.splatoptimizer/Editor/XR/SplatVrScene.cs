// Wami Splat Optimizer — VR scene of an imported environment (XR Interaction Toolkit rig, splat collisions).
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace SplatKit.EditorTools
{
    /// <summary>
    /// Escena VR de un entorno (&lt;prefijo&gt;&lt;nombre&gt;_VR.unity) para recorrerlo con el visor: rig de los Starter
    /// Assets de XR Interaction Toolkit (andar, girar, teletransporte, gravedad), colisiones sacadas de los splats
    /// (colision.json) y, opcionalmente, una mesa con agarrables y pelotas y cajas para tirar.
    /// Solo se compila si el proyecto tiene XR Interaction Toolkit.
    ///
    /// VR scene of an environment: XRI Starter Assets rig, collisions from the splats and optional props.
    /// </summary>
    [InitializeOnLoad]
    public class SplatVrScene : SplatEnvironments.IVrScenes
    {
        static SplatVrScene() => SplatEnvironments.Vr = new SplatVrScene();

        const float GroupCell = 4f;        // m: las cajas se agrupan por celdas para no crear miles de objetos

        // En VR se dibuja dos veces (un ojo cada vez): perfil automático.
        public static readonly SplatEnvironments.Variant[] Variants =
        {
            new SplatEnvironments.Variant { file = "piramide.ply", label = "LOD · VR (auto)", budget = 600_000, profile = "auto" },
            new SplatEnvironments.Variant { file = "piramide_quest.ply", label = "LOD · Quest 2 (150 k)", budget = 150_000, profile = "quest2" },
        };

        // El blaster va en el centro y tumbado (de pie sobre la empuñadura vuelca y cae de la mesa).
        static readonly string[] TableObjects = { "Cube", "Cylinder", "Blaser", "Pot", "Torus", "Torus-Cut" };

        [Serializable] class Collision { public float voxel_m; public bool nivelado; public float[] cajas; public float[] zonas_libres; }

        public string ScenePath(string folder) => SplatEnvironments.ScenePath(folder, "_VR");

        Scene SplatEnvironments.IVrScenes.Create(string folder, string path, bool props) => Create(folder, path, props);

        /// <summary>El rig de los Starter Assets de XRI (cualquier versión importada). / XRI Starter Assets rig.</summary>
        static string RigPath()
        {
            var guid = AssetDatabase.FindAssets("\"XR Origin (XR Rig)\" t:Prefab", new[] { "Assets" })
                                    .FirstOrDefault(g => AssetDatabase.GUIDToAssetPath(g).Contains("Starter Assets"));
            if (guid == null)
                throw new FileNotFoundException(SplatLang.T(
                    "No están los Starter Assets de XR Interaction Toolkit: impórtalos desde el Package Manager (Samples).",
                    "XR Interaction Toolkit Starter Assets are missing: import them from the Package Manager (Samples)."));
            return AssetDatabase.GUIDToAssetPath(guid);
        }

        /// <param name="path">dónde guardarla / where to save it (default &lt;prefix&gt;&lt;name&gt;_VR.unity)</param>
        /// <param name="props">mesa con agarrables y pelotas / table with grabbables and balls</param>
        public static Scene Create(string folder, string path = null, bool props = true)
        {
            var colPath = folder + "/colision.json";
            if (!File.Exists(colPath))
                throw new FileNotFoundException(SplatLang.T($"Falta {colPath}: vuelve a importar la escena", $"{colPath} is missing: import the scene again"));
            var col = JsonUtility.FromJson<Collision>(File.ReadAllText(colPath));
            var rigPath = RigPath();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var env = SplatEnvironments.CreateVariants(folder, Variants);

            CreateLight();
            new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();
            // Las colisiones nuevas vienen niveladas (marco del mundo); las antiguas, en el de los splats (bajo la raíz).
            var frame = col.nivelado ? null : env.transform;
            int nFloor = CreateCollisions(col, frame);
            Physics.SyncTransforms();

            Vector3 World(float x, float z) => frame ? frame.TransformPoint(new Vector3(x, 0, z)) : new Vector3(x, 0, z);
            var zones = Enumerable.Range(0, (col.zonas_libres?.Length ?? 0) / 3)
                .Select(i => World(col.zonas_libres[3 * i], col.zonas_libres[3 * i + 1])).ToList();

            // Inicio: el suelo bajo la cámara inicial si cabe una persona; si no, la zona libre más cercana.
            var (cam0, look) = SplatEnvironments.InitialPose(folder);
            var start = Floor(cam0) ?? new Vector3(cam0.x, 0, cam0.z);
            if (!Free(start, 0.3f) && zones.Count > 0)
                start = zones.OrderBy(z => (z - start).sqrMagnitude).First();

            var rig = CreateRig(rigPath, start, look);
            foreach (var lod in env.GetComponentsInChildren<SplatLod>(true))
                lod.targetCamera = rig.GetComponentInChildren<Camera>();

            Vector3? table = null, toys = null;
            if (props)
            {
                var ahead = start + look * 3f;
                var cands = zones.Where(z => (z - start).magnitude > 1.5f).OrderBy(z => (z - ahead).sqrMagnitude).ToList();
                var objects = new GameObject("Objetos / Objects (grabbable)");
                table = cands.Count > 0 ? cands[0] : Gap(start, look, 0.9f);
                if (table.HasValue) CreateTable(rigPath, table.Value, start, objects.transform);
                Physics.SyncTransforms();
                toys = cands.Count > 1 ? cands[1] : Gap(start, look, 0.6f);
                if (toys.HasValue) CreateToys(toys.Value, cands.Count > 1 ? 0.7f : 0.4f, objects.transform);
            }

            path ??= SplatEnvironments.ScenePath(folder, "_VR");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            Debug.Log($"[Wami Splat Optimizer] {path}: {col.cajas.Length / 6:N0} cajas/boxes ({nFloor:N0} suelo/floor, teleport), " +
                      $"inicio/start {start}, mesa/table {(table.HasValue ? table.Value.ToString() : "-")}, " +
                      $"juguetes/toys {(toys.HasValue ? toys.Value.ToString() : "-")}");
            return scene;
        }

        /// <summary>Punto del suelo bajo p: de lo que haya debajo, lo más cercano a y ≈ 0. / Floor point under p.</summary>
        static Vector3? Floor(Vector3 p)
        {
            var hits = Physics.RaycastAll(p + Vector3.up * 0.2f, Vector3.down, 5f);
            return hits.Length > 0 ? hits.OrderBy(h => Mathf.Abs(h.point.y)).First().point : (Vector3?)null;
        }

        static bool Free(Vector3 floor, float radius) =>
            !Physics.CheckCapsule(floor + Vector3.up * (0.25f + radius), floor + Vector3.up * 1.7f, radius);

        static Vector3? Gap(Vector3 start, Vector3 look, float radius)
        {
            foreach (var d in new[] { 1.2f, 1.8f, 2.5f, 3f })
                foreach (var ang in new[] { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 180f })
                {
                    var p = start + Quaternion.Euler(0, ang, 0) * look * d;
                    var s = Floor(p + Vector3.up * 1f);
                    if (s.HasValue && Mathf.Abs(s.Value.y - start.y) < 0.15f && Free(s.Value, radius) &&
                        !Physics.Linecast(start + Vector3.up * 1f, s.Value + Vector3.up * 1f))
                        return s.Value;
                }
            return null;
        }

        static void CreateLight()
        {
            // Los splats no se iluminan; la luz es para los objetos.
            var go = new GameObject("Luz / Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.color = new Color(1f, 0.95f, 0.86f);
            light.shadows = LightShadows.Soft;
            go.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.4f, 0.38f);
        }

        /// <summary>
        /// Una BoxCollider por caja, agrupadas por celdas, bajo <paramref name="frame"/> (null = mundo). Las losas del suelo
        /// van aparte con TeleportationArea. / One BoxCollider per box; floor slabs get a TeleportationArea.
        /// </summary>
        static int CreateCollisions(Collision col, Transform frame)
        {
            var root = new GameObject("Colisiones / Collisions (splats)");
            if (frame) root.transform.SetParent(frame, false);
            var obst = new GameObject("Obstáculos / Obstacles");
            obst.transform.SetParent(root.transform, false);
            var floor = new GameObject("Suelo / Floor (teleport)");
            floor.transform.SetParent(root.transform, false);
            var groups = new Dictionary<(bool, int, int), GameObject>();
            int nFloor = 0;
            float eps = 0.01f;
            for (int i = 0; i + 5 < col.cajas.Length; i += 6)
            {
                var c = new Vector3(col.cajas[i], col.cajas[i + 1], col.cajas[i + 2]);
                var s = new Vector3(col.cajas[i + 3], col.cajas[i + 4], col.cajas[i + 5]);
                bool isFloor = c.y + s.y / 2 <= eps && c.y - s.y / 2 >= -col.voxel_m - eps;
                if (isFloor) nFloor++;
                var key = (isFloor, Mathf.FloorToInt(c.x / GroupCell), Mathf.FloorToInt(c.z / GroupCell));
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new GameObject($"{(isFloor ? "floor" : "obstacles")}_{key.Item2}_{key.Item3}");
                    g.transform.SetParent((isFloor ? floor : obst).transform, false);
                    g.isStatic = true;
                    groups[key] = g;
                }
                var bc = g.AddComponent<BoxCollider>();
                bc.center = c;
                bc.size = s;
            }
            root.isStatic = obst.isStatic = floor.isStatic = true;

            var area = floor.AddComponent<TeleportationArea>();
            area.interactionLayers = InteractionLayerMask.GetMask("Teleport");
            area.matchOrientation = MatchOrientation.WorldSpaceUp;
            return nFloor;
        }

        static GameObject CreateRig(string rigPath, Vector3 start, Vector3 look)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rigPath);
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rig.transform.SetPositionAndRotation(start, Quaternion.LookRotation(look));
            var cam = rig.GetComponentInChildren<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            // 0,5 m (el del prefab) sube a bancos y muebles bajos; 0,3 m salva el ruido del suelo y los peldaños.
            var cc = rig.GetComponent<CharacterController>();
            if (cc) cc.stepOffset = 0.3f;
            rig.AddComponent<Respawn>();
            return rig;
        }

        static Material Mat(string name, Color color, float smooth = 0.3f)
        {
            var folder = SplatProjectSettings.Instance.materials.TrimEnd('/');
            var path = $"{folder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!mat)
            {
                Directory.CreateDirectory(folder);
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            mat.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Mesa de 1,6 × 0,8 m de cara al inicio con los agarrables de los Starter Assets encima.</summary>
        static void CreateTable(string rigPath, Vector3 pos, Vector3 start, Transform parent)
        {
            var wood = Mat("Mesa", new Color(0.45f, 0.3f, 0.18f), 0.35f);
            var table = new GameObject("Mesa / Table");
            table.transform.SetParent(parent, false);
            var toStart = Vector3.ProjectOnPlane(start - pos, Vector3.up);
            table.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(toStart.sqrMagnitude > 0.01f ? toStart.normalized : Vector3.back));
            table.isStatic = true;
            const float h = 0.8f, w = 1.6f, d = 0.8f, top = 0.05f;
            Box("Tablero", table.transform, new Vector3(0, h - top / 2, 0), new Vector3(w, top, d), wood).isStatic = true;
            foreach (var (sx, sz) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                Box("Pata", table.transform, new Vector3(sx * (w / 2 - 0.06f), (h - top) / 2, sz * (d / 2 - 0.06f)),
                    new Vector3(0.06f, h - top, 0.06f), wood).isStatic = true;

            // Los agarrables de la demo de los Starter Assets (junto al rig).
            var starter = rigPath.Substring(0, rigPath.IndexOf("/Prefabs/", StringComparison.Ordinal));
            var prefabs = TableObjects.Select(n => AssetDatabase.LoadAssetAtPath<GameObject>($"{starter}/DemoAssets/Prefabs/Interactables/{n}.prefab"))
                                      .Where(p => p).ToArray();
            for (int i = 0; i < prefabs.Length; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[i], parent);
                float x = -w / 2 + w * (i + 0.5f) / prefabs.Length;
                var rot = table.transform.rotation * (prefabs[i].name == "Blaser" ? Quaternion.Euler(90, 0, 0) : Quaternion.identity);
                go.transform.SetPositionAndRotation(table.transform.TransformPoint(new Vector3(x, h, 0)), rot);
                Rest(go, table.transform.position.y + h);
                go.AddComponent<Respawn>();
            }
        }

        /// <summary>Apoya el objeto 1 cm sobre la altura dada (por sus colliders). / Rest the object on a height.</summary>
        static void Rest(GameObject go, float y)
        {
            Physics.SyncTransforms();
            var cols = go.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if (cols.Length == 0) return;
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            go.transform.position += Vector3.up * (y + 0.01f - b.min.y);
        }

        static void CreateToys(Vector3 centre, float radius, Transform parent)
        {
            var colors = new[]
            {
                new Color(0.85f, 0.2f, 0.15f), new Color(0.15f, 0.45f, 0.85f), new Color(0.95f, 0.75f, 0.1f),
                new Color(0.2f, 0.7f, 0.3f), new Color(0.6f, 0.25f, 0.75f),
            };
            int n = radius < 0.6f ? 6 : 8;
            for (int i = 0; i < n; i++)
            {
                bool ball = i % 2 == 0;
                float size = ball ? 0.22f + 0.04f * (i % 3) : 0.25f + 0.05f * (i % 3);
                if (radius < 0.6f) size *= 0.7f;
                var go = GameObject.CreatePrimitive(ball ? PrimitiveType.Sphere : PrimitiveType.Cube);
                go.name = ball ? $"Pelota / Ball {i / 2 + 1}" : $"Caja / Box {i / 2 + 1}";
                go.transform.SetParent(parent, false);
                float ang = i * Mathf.PI * 2 / n;
                go.transform.position = centre + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * radius + Vector3.up * (size / 2 + 0.02f);
                go.transform.localScale = Vector3.one * size;
                go.GetComponent<Renderer>().sharedMaterial =
                    Mat(ball ? $"Pelota_{i % colors.Length}" : $"Caja_{i % colors.Length}", colors[i % colors.Length], ball ? 0.6f : 0.2f);
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = ball ? 0.4f : 1.2f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                var grab = go.AddComponent<XRGrabInteractable>();
                grab.throwOnDetach = true;
                go.AddComponent<Respawn>();
            }
        }

        // ------------------------------------------------------------------ verificación sin visor / headless checks

        /// <summary>
        /// -batchmode -executeMethod SplatKit.EditorTools.SplatVrScene.BatchCreateAndCapture [-entorno X] (sin -quit):
        /// crea la escena VR, comprueba la física y captura &lt;capturas&gt;/entorno_X_vr_N.png.
        /// </summary>
        public static void BatchCreateAndCapture() => SplatEnvironments.RunBatch(Steps());

        static IEnumerator<object> Steps()
        {
            var only = SplatEnvironments.ArgEnvironment();
            var folders = SplatEnvironments.Folders()
                .Where(c => File.Exists(c + "/colision.json") && (only == null || Path.GetFileName(c) == only)).ToArray();
            if (folders.Length == 0) throw new FileNotFoundException($"colision.json: {SplatEnvironments.Root}");
            foreach (var c in folders)
            {
                var it = CaptureSteps(c);
                while (it.MoveNext()) yield return it.Current;
            }
        }

        static IEnumerator<object> CaptureSteps(string folder)
        {
            Create(folder);
            var rig = UnityEngine.Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>().gameObject;
            var start = rig.transform.position;
            Physics.SyncTransforms();
            Verify(rig);
            yield return null;

            var env = UnityEngine.Object.FindAnyObjectByType<SplatVariants>();
            var gs = env.variants[0].target.GetComponent<GsplatRenderer>();
            var lod = gs.GetComponent<SplatLod>();
            gs.Tick();
            yield return null;

            var camGo = new GameObject("Capture Camera");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<UniversalAdditionalCameraData>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 90f;
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            cam.targetTexture = rt;

            var eyes = start + Vector3.up * 1.6f;
            var views = new List<(Vector3 pos, Vector3 at)> { (eyes, eyes + rig.transform.forward) };
            var table = GameObject.Find("Mesa / Table");
            if (table)
            {
                var m = table.transform;
                views.Add((m.position + m.forward * 1.6f + Vector3.up * 1.5f, m.position + Vector3.up * 0.8f));
            }
            var prefix = $"entorno_{Path.GetFileName(folder)}_vr";
            for (int v = 0; v < views.Count; v++)
            {
                camGo.transform.SetPositionAndRotation(views[v].pos, Quaternion.LookRotation(views[v].at - views[v].pos));
                lod.targetCamera = cam;
                lod.Refresh(cam, true);
                for (int f = 0; f < 3; f++)
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
                File.WriteAllBytes(Path.Combine(SplatEnvironments.CapturesFolder, $"{prefix}_{v + 1}.png"), tex.EncodeToPNG());
                yield return null;
            }
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
        }

        static void Verify(GameObject rig)
        {
            var start = rig.transform.position;
            if (Physics.Raycast(start + Vector3.up * 1.5f, Vector3.down, out var hit, 5f))
                Debug.Log($"[Wami Splat Optimizer] Suelo/floor y = {hit.point.y:F2} ({hit.collider.name})");
            else
                Debug.LogError("[Wami Splat Optimizer] No hay suelo bajo el inicio / no floor under the start");

            var cc = rig.GetComponent<CharacterController>();
            var t = rig.transform;
            foreach (var (dir, name) in new[] { (t.forward, "forward"), (-t.forward, "back"), (-t.right, "left"), (t.right, "right") })
            {
                cc.enabled = false;
                rig.transform.position = start;
                cc.enabled = true;
                Physics.SyncTransforms();
                for (int i = 0; i < 400; i++) cc.Move(dir * 0.1f + Vector3.down * 0.05f);
                var p = rig.transform.position;
                Debug.Log($"[Wami Splat Optimizer] Walk {name}: {Vector3.ProjectOnPlane(p - start, Vector3.up).magnitude:F1} m (y = {p.y:F2})");
            }
            cc.enabled = false;
            rig.transform.position = start;
            cc.enabled = true;

            var bodies = UnityEngine.Object.FindObjectsByType<XRGrabInteractable>(FindObjectsSortMode.None)
                .Select(g => g.GetComponent<Rigidbody>()).Where(r => r && !r.isKinematic).ToArray();
            var before = bodies.Select(r => r.transform.position).ToArray();
            var mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            for (int i = 0; i < 180; i++) Physics.Simulate(1f / 60f);
            Physics.simulationMode = mode;
            for (int i = 0; i < bodies.Length; i++)
            {
                float drop = before[i].y - bodies[i].transform.position.y;
                var msg = $"[Wami Splat Optimizer] {bodies[i].name}: y {before[i].y:F2} → {bodies[i].transform.position.y:F2}";
                if (drop > 0.3f) Debug.LogError(msg + " (se cae / falls)");
                else Debug.Log(msg);
            }
        }
    }
}
