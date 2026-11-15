"""
SplatKit — cadena offline para escenas 3D Gaussian Splatting / offline pipeline for 3DGS scenes.

    splatkit info <escena.ply>
    splatkit preparar <escena.ply> --nombre X [--colmap <sparse/0>] [--unity <proyecto>] [--perfiles quest2,pcvr_low]
    splatkit perfiles

Las salidas de cada escena van a <espacio>/<nombre>/ (``--espacio`` o la variable SPLATKIT_WORKSPACE; por defecto
./splatkit_escenas). Los PLY de varios GB se leen por bloques. ``--lang es|en`` cambia el idioma.

Outputs go to <workspace>/<name>/ (``--espacio`` or SPLATKIT_WORKSPACE; default ./splatkit_escenas).
Multi-GB PLY files are read in blocks. ``--lang es|en`` switches the language.
"""

import argparse
import json
import math
import os
import sys
import time

import numpy as np

if __package__ in (None, ""):                       # ejecutado como script suelto
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    __package__ = "splatkit"

from splatkit import analisis, profiles, vista  # noqa: E402
from splatkit.i18n import pick, set_lang, t  # noqa: E402
from splatkit.ply import SplatPly  # noqa: E402

UNITY_DIR_DEFAULT = os.environ.get("SPLATKIT_UNITY_DIR", "Assets/Splats")


def workspace(a=None):
    w = getattr(a, "espacio", None) or os.environ.get("SPLATKIT_WORKSPACE") or os.path.join(os.getcwd(),
                                                                                          "splatkit_escenas")
    return os.path.abspath(w)


def entorno_dir(ply_path, nombre=None, a=None):
    """
    <espacio>/<nombre>/original/x.ply -> <espacio>/<nombre>/. Un PLY de fuera con `nombre` -> <espacio>/<nombre>/
    (los resultados nunca se escriben junto al original). Sin nombre, su carpeta.
    """
    d = os.path.dirname(os.path.abspath(ply_path))
    if os.path.basename(d) == "original":
        return os.path.dirname(d)
    if nombre:
        out = os.path.join(workspace(a), nombre)
        os.makedirs(out, exist_ok=True)
        return out
    return d


def _dump(path, data, indent=2):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=indent, ensure_ascii=False)


def _load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


# ------------------------------------------------------------------------------------------------ info

def cmd_info(a):
    t0 = time.time()
    ply = SplatPly.open(a.ply)
    print(f"{ply.count:,} splats · SH{ply.sh_degree} · {ply.bytes_per_splat} B/splat · "
          f"{ply.count * ply.bytes_per_splat / 2**30:.2f} GB")
    core = analisis.load_core(ply)
    print(t("leído en {s:.0f} s", "read in {s:.0f} s", s=time.time() - t0))
    rep, extra = analisis.inspect(ply, core)
    out = a.salida or os.path.join(entorno_dir(a.ply, a.nombre, a), "analisis")
    os.makedirs(out, exist_ok=True)
    _dump(os.path.join(out, "informe.json"), rep)

    if not a.sin_vistas:
        pos, color, alpha = core["pos"], core["color"], core["alpha"]
        bounds = (np.array(rep["zona_principal"]["min"]), np.array(rep["zona_principal"]["max"]))
        aislados = extra["inside"] & (extra["neigh"] < 4)
        for view in "xyz":
            ims = [vista.ortho(pos, color, alpha, view, False, a.res, "cercano", bounds=bounds),
                   vista.ortho(pos, color, alpha, view, True, a.res, "cercano", bounds=bounds),
                   vista.ortho(pos, color, alpha, view, False, a.res, "densidad", bounds=bounds,
                               highlight=aislados)]
            vista.save_contact_sheet(ims, [t("desde +{v}", "from +{v}", v=view), t("desde -{v}", "from -{v}", v=view),
                                           t("densidad {v} (rojo: aislados)", "density {v} (red: isolated)", v=view)],
                                     os.path.join(out, f"vista_{view}.png"))
    print_summary(rep)
    print(t("\nInforme y vistas en {o} ({s:.0f} s)", "\nReport and previews in {o} ({s:.0f} s)",
            o=out, s=time.time() - t0))
    return 0


# --------------------------------------------------------------------------------------------- cámaras

def _quat_wxyz_to_mat(q):
    w, x, y, z = q
    return np.array([[1 - 2 * y * y - 2 * z * z, 2 * x * y - 2 * w * z, 2 * z * x + 2 * w * y],
                     [2 * x * y + 2 * w * z, 1 - 2 * x * x - 2 * z * z, 2 * y * z - 2 * w * x],
                     [2 * z * x - 2 * w * y, 2 * y * z + 2 * w * x, 1 - 2 * x * x - 2 * y * y]])


def lichtfeld_to_unity(path_json, man, max_vistas=12):
    """
    Camino de cámara de LichtFeld Studio (``{"keyframes": [{position, rotation (w,x,y,z), focal_length_mm}]}``,
    mundo del visor = diag(1,-1,-1) · COLMAP) -> vistas del banco en ejes de Unity de la escena preparada (con la
    rotación de nivelación, igual que la cámara inicial de ``preparar``).
    """
    keys = _load(path_json)["keyframes"]
    D = np.diag([1.0, -1.0, -1.0])
    idx = np.unique(np.linspace(0, len(keys) - 1, min(max_vistas, len(keys))).round().astype(int))
    vistas = []
    for j, i in enumerate(idx):
        k = keys[i]
        c = D @ np.asarray(k["position"], np.float64)
        r_c2w = D @ _quat_wxyz_to_mat(k["rotation"]) @ D
        cam = camara_inicio(man, (c, r_c2w[:, 2]), 0.0, keep_pitch=True)
        p = np.asarray(cam["pos"])
        vistas.append({"nombre": f"v{j:02d}", "pos": p.tolist(), "mirar": (p + np.asarray(cam["adelante"])).tolist()})
    focal = keys[0].get("focal_length_mm")
    return vistas, (math.degrees(2 * math.atan(12.0 / focal)) if focal else None)


def cmd_camaras(a):
    """Cámaras de prueba para el banco de Unity (ejes de Unity)."""
    if a.lichtfeld:
        man_path = a.ply[:-4] + ".json"
        if not os.path.exists(man_path):
            raise SystemExit(t("--lichtfeld necesita una escena preparada (falta {p})",
                               "--lichtfeld needs a prepared scene ({p} is missing)", p=man_path))
        man = _load(man_path)
        vistas, fov_lf = lichtfeld_to_unity(a.lichtfeld, man, a.max_vistas)
        name = "camaras_" + os.path.splitext(os.path.basename(a.lichtfeld))[0] + ".json"
        out = {"fov_v": a.fov or round(fov_lf or 90.0, 2), "marco": "unity", "vistas": vistas,
               "nota": t("Ejes de Unity de la escena preparada (rotación de nivelación aplicada). Origen: {o}",
                         "Unity axes of the prepared scene (levelling rotation applied). Source: {o}",
                         o=os.path.basename(a.lichtfeld))}
        path = os.path.join(entorno_dir(a.ply, None, a), name)
        _dump(path, out)
        print(t("{n} cámaras en {p}", "{n} cameras in {p}", n=len(vistas), p=path))
        return 0

    ply = SplatPly.open(a.ply)
    core = analisis.load_core(ply)
    rep = _load(os.path.join(entorno_dir(a.ply, None, a), "analisis", "informe.json"))
    suelo = rep["suelo"]
    if suelo["eje"] != "-y":
        raise SystemExit(t("Solo implementado para suelo con arriba -y (RDF); este: {e}",
                           "Only implemented for floors with up = -y (RDF); this one: {e}", e=suelo["eje"]))
    pos, alpha = core["pos"], core["alpha"]
    fy = suelo["punto"][1]
    floor = pos[(alpha > 0.5) & (np.abs(pos[:, 1] - fy) < 0.15)]
    x5, x50, x98 = np.percentile(floor[:, 0], [5, 50, 98])
    z2, z50, z98 = np.percentile(floor[:, 2], [2, 50, 98])
    side = pos[(alpha > 0.5) & (pos[:, 0] < x5 - 2.0) & (np.abs(pos[:, 1] - fy) < 3)]
    eye = -fy + a.ojos                     # Unity: y = -y_datos
    xc = float(x50)

    def v(nombre, p, m):
        return {"nombre": nombre, "pos": [float(c) for c in p], "mirar": [float(c) for c in m]}

    vistas = [
        v("entrada", (xc, eye, z2 + 3), (xc, eye, z98)),
        v("nave", (xc, eye, z50), (xc, eye, z98)),
        v("nave_atras", (xc, eye, z50), (xc, eye, z2)),
        v("boveda", (xc, eye, z50), (xc, eye + 10, z50 + 3)),
        v("altar", (xc, eye, z98 - 6), (xc, eye - 0.3, z98)),
        v("pared_cerca", (x98 - 1.2, eye, z50), (x98, eye, z50)),
    ]
    if len(side) > 1000:
        zc = float(np.median(side[:, 2]))
        vistas.append(v("capilla", (xc, eye, zc), (float(np.percentile(side[:, 0], 5)), eye, zc)))
    name = "camaras.json"
    if a.recorrido:
        rng = np.random.default_rng(a.semilla)
        cand = floor[rng.permutation(len(floor))[:200000]]
        picked = []
        for p in cand:
            if all(np.hypot(p[0] - q[0], p[2] - q[2]) >= 3.0 for q in picked):
                picked.append(p)
            if len(picked) >= a.recorrido:
                break
        vistas = []
        for i, p in enumerate(picked):
            yaw = rng.uniform(0, 2 * np.pi)
            pitch = np.radians(rng.uniform(-10, 25))
            d = (np.cos(pitch) * np.sin(yaw), np.sin(pitch), np.cos(pitch) * np.cos(yaw))
            pp = (float(p[0]), eye, float(p[2]))
            vistas.append(v(f"r{i:02d}", pp, (pp[0] + d[0], pp[1] + d[1], pp[2] + d[2])))
        name = "camaras_recorrido.json"
    out = {"fov_v": a.fov or 90.0, "vistas": vistas,
           "nota": t("Ejes de Unity (RDF importado: y = -y_datos). Suelo y = {f:.3f}, ojos a {o} m.",
                     "Unity axes (imported as RDF: y = -y_data). Floor y = {f:.3f}, eyes at {o} m.", f=-fy, o=a.ojos)}
    path = os.path.join(entorno_dir(a.ply, None, a), name)
    _dump(path, out)
    print(t("{n} cámaras en {p}", "{n} cameras in {p}", n=len(vistas), p=path))
    return 0


# --------------------------------------------------------------------------------------------- limpiar

def cmd_limpiar(a):
    from splatkit import limpieza
    t0 = time.time()
    ply = SplatPly.open(a.ply)
    core = analisis.load_core(ply)
    rep, extra = analisis.inspect(ply, core)
    o = limpieza.Opciones(margen_zona=a.margen, min_vecinos=a.vecinos, min_opacidad=a.opacidad,
                          max_sigma_diag=a.gigante, sh=a.sh)
    base = entorno_dir(a.ply, None, a)
    variants = [int(s) for s in a.variantes_sh.split(",")] if a.variantes_sh else [a.sh]
    for sh in variants:
        o.sh = sh
        name = f"{a.nombre}_sh{sh}"
        out = os.path.join(base, name + ".ply")
        man = limpieza.clean(ply, out, o, core, rep, extra)
        man["licencia"] = leer_licencia(a.ply)
        for p in man["partes"]:                     # un manifiesto por parte (el banco recoloca cada una)
            _dump(p[:-4] + ".json", man)
        size = sum(os.path.getsize(p) for p in man["partes"]) / 2**20
        print(t("{n}: {s:,} splats ({pc:.1f} %), {mb:.0f} MB en {k} archivo(s) · quitados {q}",
                "{n}: {s:,} splats ({pc:.1f} %), {mb:.0f} MB in {k} file(s) · removed {q}",
                n=name, s=man["splats_salida"], pc=100 * man["splats_salida"] / ply.count, mb=size,
                k=len(man["partes"]), q=man["quitados"]))
    print(f"({time.time() - t0:.0f} s)")
    return 0


# ------------------------------------------------------------------------------------- estación y LOD

def station_setup(a):
    ply = SplatPly.open(a.ply)
    man_in_path = a.ply[:-4] + ".json"
    if not os.path.exists(man_in_path):
        raise SystemExit(t("La entrada debe ser una salida de 'limpiar' (suelo en y = 0, con su .json)",
                           "The input must be an output of 'limpiar' (floor at y = 0, with its .json)"))
    man_in = _load(man_in_path)
    tr = np.array(man_in["traslacion_restada"])
    cams = _load(os.path.join(entorno_dir(a.ply, None, a), "camaras.json"))
    if a.vista:
        ux, _, uz = next(v["pos"] for v in cams["vistas"] if v["nombre"] == a.vista)
    else:
        ux, uz = (float(c) for c in a.centro.split(","))
    center = np.array([ux - tr[0], 0.0, uz - tr[2]])
    return ply, man_in, cams, (ux, uz), center


def cmd_estacion(a):
    """Poda por importancia desde una estación: <escena>/estacion_<nombre>_<K>k.ply."""
    from splatkit import estacion
    from splatkit.ply import write_ply
    t0 = time.time()
    ply, man_in, cams, (ux, uz), center = station_setup(a)
    base = entorno_dir(a.ply, None, a)
    core = analisis.load_core(ply)
    pts = estacion.view_points(center, radius=a.radio)
    imp = estacion.importance(core["pos"], core["alpha"], core["sigma"], core["finite"], pts,
                              max_dist=a.distancia, normal=core["normal"])
    visible = int((imp > 0).sum())
    print(t("Estación '{n}' en Unity ({x:.2f}, {z:.2f}): {p} puntos de vista, {v:,} splats visibles de {c:,} ({s:.0f} s)",
            "Station '{n}' at Unity ({x:.2f}, {z:.2f}): {p} viewpoints, {v:,} visible splats of {c:,} ({s:.0f} s)",
            n=a.nombre, x=ux, z=uz, p=len(pts), v=visible, c=ply.count, s=time.time() - t0))
    for k in [int(float(s) * 1000) for s in a.k.split(",")]:
        keep = estacion.top_k(imp, k)
        name = f"estacion_{a.nombre}_{k // 1000}k"
        out = os.path.join(base, name + ".ply")
        n = write_ply(out, ply, mask=keep, sh_degree=a.sh,
                      comments=[f"estacion {a.nombre}: top {k} por importancia desde {len(pts)} puntos"])
        cubierto = float(imp[keep].sum() / max(imp.sum(), 1e-30))
        man = dict(man_in, salida=out, partes=[out], splats_salida=n, sh_salida=min(a.sh, ply.sh_degree),
                   estacion={"nombre": a.nombre, "centro_unity_xz": [ux, uz], "radio": a.radio, "k": k,
                             "visibles": visible, "importancia_cubierta": cubierto, "fuente": a.ply})
        _dump(os.path.join(base, name + ".json"), man)
        print(t("  {nm}: {n:,} splats, {mb:.0f} MB, {pc:.1f} % de la importancia total",
                "  {nm}: {n:,} splats, {mb:.0f} MB, {pc:.1f} % of the total importance",
                nm=name, n=n, mb=os.path.getsize(out) / 2**20, pc=100 * cubierto))
    floor_unity = -man_in["traslacion_restada"][1]
    eye = floor_unity + 1.6
    vistas = []
    for ang in range(0, 360, 60):
        d = np.radians(ang)
        vistas.append({"nombre": f"e{ang:03d}", "pos": [ux, eye, uz], "mirar": [ux + np.sin(d), eye, uz + np.cos(d)]})
    vistas.append({"nombre": "e_boveda", "pos": [ux, eye, uz], "mirar": [ux, eye + 3, uz + 1]})
    for off, nm in (((a.radio, 0), "borde_x"), ((0, -a.radio), "borde_z")):
        vistas.append({"nombre": nm, "pos": [ux + off[0], eye, uz + off[1]], "mirar": [ux + off[0], eye, uz + 10]})
    path = os.path.join(base, f"camaras_estacion_{a.nombre}.json")
    _dump(path, {"fov_v": cams.get("fov_v", 90), "vistas": vistas})
    print(t("Cámaras de la estación en {p}", "Station cameras in {p}", p=path))
    return 0


def cmd_lod(a):
    """LOD por fusión desde una estación: <escena>/lod_<nombre>_<θ>.ply por cada ángulo."""
    import gc
    from splatkit import estacion, lod
    from splatkit.ply import write_columns
    t0 = time.time()
    ply, man_in, cams, (ux, uz), center = station_setup(a)
    base = entorno_dir(a.ply, None, a)
    up = np.array([0.0, -1.0, 0.0])
    eye = center + a.ojos * up
    visible = None
    if not a.sin_oclusion:
        core = analisis.load_core(ply)
        pts = estacion.view_points(center, radius=a.radio)
        imp = estacion.importance(core["pos"], core["alpha"], core["sigma"], core["finite"], pts,
                                  normal=core["normal"])
        visible = (imp > 0) & core["finite"]
        del core, imp
        gc.collect()
        print(t("Oclusión: {v:,} splats visibles de {c:,} ({s:.0f} s)", "Occlusion: {v:,} visible splats of {c:,} ({s:.0f} s)",
                v=int(visible.sum()), c=ply.count, s=time.time() - t0))
    cols = lod.read_columns(ply, visible, sh_degree=a.sh)
    pos = np.stack([cols["x"], cols["y"], cols["z"]], 1)
    for ang in [float(s) for s in a.angulos.split(",")]:
        theta = np.radians(ang)
        key, size = lod.cell_keys(pos, eye, theta, r_station=a.radio, radial=a.radial)
        out, st = lod.merge(cols, key, size, inflate=a.inflar)
        name = (f"lod_{a.nombre}_{ang:g}" + (f"_r{a.radial:g}" if a.radial != 1 else "")
                + (f"_inf{a.inflar:g}" if a.inflar > 0 else "")).replace(".", "p")
        path = os.path.join(base, name + ".ply")
        n = write_columns(path, out, comments=[
            f"lod {a.nombre}: fusion desde la estacion, theta {ang} grados, radio {a.radio} m, inflado {a.inflar}"])
        man = dict(man_in, salida=path, partes=[path], splats_salida=n, sh_salida=min(a.sh, ply.sh_degree),
                   lod={"nombre": a.nombre, "centro_unity_xz": [ux, uz], "radio": a.radio, "theta_grados": ang,
                        "inflado": a.inflar, "radial": a.radial, "oclusion": not a.sin_oclusion,
                        "fuente": a.ply, **st})
        _dump(os.path.join(base, name + ".json"), man)
        print(t("  {nm}: {n:,} splats ({i:,} intactos + {f:,} fusionadas de {sf:,}), {mb:.0f} MB ({s:.0f} s)",
                "  {nm}: {n:,} splats ({i:,} intact + {f:,} merged from {sf:,}), {mb:.0f} MB ({s:.0f} s)",
                nm=name, n=n, i=st["intactos"], f=st["fusionadas"], sf=st["splats_fusionados"],
                mb=os.path.getsize(path) / 2**20, s=time.time() - t0))
    return 0


def cmd_piramide(a):
    """Pirámide LOD por trozos de la escena completa: <escena>/<nombre>.ply + .json."""
    from splatkit import piramide
    t0 = time.time()
    ply = SplatPly.open(a.ply)
    man_in_path = a.ply[:-4] + ".json"
    man_in = _load(man_in_path) if os.path.exists(man_in_path) else {}
    out = os.path.join(entorno_dir(a.ply, None, a), a.nombre + ".ply")
    voxels = tuple(float(v) for v in a.voxeles.split(","))
    man = piramide.build(ply, out, trozo=a.trozo, voxels=voxels, sh_degree=a.sh, nivel_minimo=a.nivel_minimo)
    man["traslacion_restada"] = man_in.get("traslacion_restada")
    man["licencia"] = man_in.get("licencia")
    with open(out[:-4] + ".json", "w", encoding="utf-8") as f:
        json.dump(man, f, ensure_ascii=False)
    print(f"{out}: {os.path.getsize(out) / 2**20:.0f} MB ({time.time() - t0:.0f} s)")
    return 0


# -------------------------------------------------------------------------------------------- preparar

def cmd_preparar(a):
    """
    Cadena completa para una escena nueva: análisis + vistas previas, limpieza (SH1, trasladada, sin partir),
    pirámides LOD de los perfiles pedidos, colisiones y, con --unity, exportación al proyecto Unity.
    """
    from splatkit import colision, limpieza, piramide
    t0 = time.time()
    perfiles = [p.strip() for p in a.perfiles.split(",")] if a.perfiles else None
    if a.piramides:
        wanted = [x.strip() for x in a.piramides.split(",") if x.strip()]
        pirs = [profiles.pyramid(x) for x in wanted]
        pirs = [x for x in profiles.load()["pyramids"] if x in pirs]              # orden del archivo
    elif perfiles:
        pirs = profiles.pyramids_for(perfiles)
    else:
        pirs = [profiles.pyramid(x) for x in profiles.load()["default_pyramids"]]
    ply = SplatPly.open(a.ply)
    base = entorno_dir(a.ply, a.nombre, a)
    print(t("[1/4] Análisis: {n:,} splats · SH{sh} · {gb:.2f} GB", "[1/4] Analysis: {n:,} splats · SH{sh} · {gb:.2f} GB",
            n=ply.count, sh=ply.sh_degree, gb=ply.count * ply.bytes_per_splat / 2**30))
    core = analisis.load_core(ply)
    rep, extra = analisis.inspect(ply, core)
    out_info = os.path.join(base, "analisis")
    os.makedirs(out_info, exist_ok=True)
    _dump(os.path.join(out_info, "informe.json"), rep)
    print_summary(rep)
    escala, inicio = a.escala or 1.0, None
    if a.colmap:
        # COLMAP no tiene gravedad ni escala: suelo guiado por las cámaras y escala por la altura del móvil.
        from splatkit import colmap
        images = a.colmap if a.colmap.endswith(".txt") else os.path.join(a.colmap, "images.txt")
        centers, ups, fwds = colmap.read_cameras(images)
        suelo = colmap.floor_from_cameras(core["pos"], core["alpha"], core["finite"], centers, ups)
        rep["suelo"] = suelo
        if not a.escala:
            escala = a.altura_camara / suelo["altura_camaras"]
        print(t("  Cámaras COLMAP: {n} · suelo con normal {nr} ({ang:.1f}° del arriba medio de las cámaras, {i:,} splats) · "
                "cámaras a {h:.2f} unidades del suelo → escala {e:.4f} m/unidad (supuesto: cámara a {ac} m)",
                "  COLMAP cameras: {n} · floor normal {nr} ({ang:.1f}° from the cameras' mean up, {i:,} splats) · "
                "cameras {h:.2f} units above the floor → scale {e:.4f} m/unit (assumed camera height {ac} m)",
                n=len(centers), nr=np.round(suelo["normal_arriba"], 3).tolist(), ang=suelo["angulo_con_camaras"],
                i=suelo["inliers"], h=suelo["altura_camaras"], e=escala, ac=a.altura_camara))
        mid = int(np.argmin(np.linalg.norm(centers - np.median(centers, 0), axis=1)))
        inicio = (centers[mid], fwds[mid])
        _dump(os.path.join(out_info, "informe.json"), rep)
    elif rep["suelo"]["inclinacion_grados"] > 10:
        print(t("  ! El suelo está inclinado {g:.1f}° respecto al eje {e}: revisa las vistas de {o} (la nivelación se aplica en Unity)",
                "  ! The floor is tilted {g:.1f}° from the {e} axis: check the previews in {o} (levelling is applied in Unity)",
                g=rep["suelo"]["inclinacion_grados"], e=rep["suelo"]["eje"], o=out_info))

    print(t("[2/4] Limpieza", "[2/4] Cleanup"))
    # El nivel «max» conserva todos los SH del original: la escena limpia se hace con el mayor grado pedido.
    sh_pir = {x["id"]: (ply.sh_degree if x["id"] == "max" else min(a.sh, x["sh"])) for x in pirs}
    clean_sh = max(sh_pir.values())
    o = limpieza.Opciones(sh=clean_sh, max_sigma_diag=a.gigante, agujas_ratio=a.agujas, min_vecinos=a.vecinos)
    clean_path = os.path.join(base, f"limpio_sh{clean_sh}.ply")
    man = limpieza.clean(ply, clean_path, o, core, rep, extra, max_bytes=float("inf"), escala=escala)
    man["licencia"] = leer_licencia(a.ply)
    _dump(clean_path[:-4] + ".json", man)
    print(t("  {n:,} splats ({pc:.1f} %) · quitados {q}", "  {n:,} splats ({pc:.1f} %) · removed {q}",
            n=man["splats_salida"], pc=100 * man["splats_salida"] / ply.count, q=man["quitados"]))
    del core, extra
    camara = camara_inicio(man, inicio, a.altura_camara)
    print(t("  Escala aplicada {e:.4f} · cámara inicial en Unity {p}", "  Applied scale {e:.4f} · initial Unity camera {p}",
            e=escala, p=np.round(camara["pos"], 2).tolist()))

    antialias = detectar_antialias(a.ply) if a.antialias is None else a.antialias
    if antialias:
        print(t("  Entrenada con antialias (Mip-Splatting): Unity compensará la opacidad del filtro 2D",
                "  Trained with antialiasing (Mip-Splatting): Unity will compensate the 2D filter opacity"))
    print(t("[3/4] Pirámides LOD ({l})", "[3/4] LOD pyramids ({l})", l=", ".join(p["id"] for p in pirs)))
    clean = SplatPly.open(clean_path)
    # Trozos y niveles a la medida de la escena: en una pequeña, trozos de 3 m dejarían muy pocos y el salto entre el
    # nivel 0 y el de 4 cm sería demasiado grande para repartir el presupuesto.
    diag_m = rep["zona_principal"]["diagonal"] * escala
    trozo = a.trozo or float(np.clip(diag_m / 20, 1.0, 3.0))
    voxeles = (0.04, 0.08, 0.16, 0.32, 0.64, 1.28)
    if clean.count < 3_000_000:
        voxeles = (0.02,) + voxeles
    print(t("  escena de {d:.1f} m de diagonal: trozos de {tr:.2f} m, vóxeles {v}",
            "  scene diagonal {d:.1f} m: {tr:.2f} m chunks, voxels {v}", d=diag_m, tr=trozo, v=list(voxeles)))
    outputs = []
    for pyr in pirs:
        name = pyr["file"]
        nivel_min = a.nivel_bajo if (pyr["id"] == "low" and a.nivel_bajo is not None) else pyr["min_level"]
        sh = sh_pir[pyr["id"]]
        path = os.path.join(base, name + ".ply")
        pm = piramide.build(clean, path, trozo=trozo, voxels=voxeles, sh_degree=sh, nivel_minimo=nivel_min)
        # gsplat no lee PLY de más de 2 GB: subir el nivel mínimo hasta caber.
        while pyr["id"] == "max" and os.path.getsize(path) > 1.9 * 2**30 and sh > 0:
            sh -= 1                         # en «max», antes perder SH que los splats originales
            print(t("  ! {n}: más de 1,9 GB; SH{s}", "  ! {n}: over 1.9 GB; SH{s}", n=name, s=sh))
            pm = piramide.build(clean, path, trozo=trozo, voxels=voxeles, sh_degree=sh, nivel_minimo=nivel_min)
        while os.path.getsize(path) > 1.9 * 2**30 and pm["nivel_minimo"] < len(pm["niveles_voxel_m"]) - 1:
            print(t("  ! {n}: {gb:.2f} GB > 1,9 GB; nivel mínimo {l}", "  ! {n}: {gb:.2f} GB > 1.9 GB; min level {l}",
                    n=name, gb=os.path.getsize(path) / 2**30, l=pm["nivel_minimo"] + 1))
            pm = piramide.build(clean, path, trozo=trozo, voxels=voxeles, sh_degree=sh,
                                nivel_minimo=pm["nivel_minimo"] + 1)
        pm.update(traslacion_restada=man["traslacion_restada"], rotacion_unity=man["rotacion_unity"],
                  escala_aplicada=escala, camara_inicio=camara, licencia=man["licencia"], nombre=a.nombre,
                  coordenadas="RDF", piramide=pyr["id"], etiqueta=pyr["label"], antialias=antialias,
                  perfiles=[p for p in (perfiles or profiles.ids()) if profiles.get(p)["bake"]["pyramid"] == pyr["id"]])
        with open(path[:-4] + ".json", "w", encoding="utf-8") as f:
            json.dump(pm, f, ensure_ascii=False)
        outputs.append(path)
        print(t("  {n}: {s:,} splats, SH{sh}, {mb:.0f} MB", "  {n}: {s:,} splats, SH{sh}, {mb:.0f} MB",
                n=name, s=pm["splats_total"], sh=sh, mb=os.path.getsize(path) / 2**20))

    # Colisiones para la escena VR: metros, con la rotación de nivelación ya aplicada (suelo en y = 0).
    cman, occ, corigin = colision.build(clean, colision.Opciones(), rotacion=man["rotacion_unity"])
    col_path = os.path.join(base, "colision.json")
    with open(col_path, "w", encoding="utf-8") as f:
        json.dump(cman, f, ensure_ascii=False)
    colision.vista_planta(occ, corigin, colision.Opciones().voxel, os.path.join(base, "colision_planta.png"))
    print(t("  colisiones: {c:,} cajas, {z} zonas libres", "  collisions: {c:,} boxes, {z} free zones",
            c=len(cman["cajas"]) // 6, z=len(cman.get("zonas_libres", [])) // 3))

    if a.unity:
        print(t("[4/4] Exportación a Unity", "[4/4] Export to Unity"))
        exportar_unity(a.unity, a.nombre, outputs, leer_licencia(a.ply), a.unity_carpeta)
        exportar_texto(a.unity, a.nombre, col_path, a.unity_carpeta)
    else:
        print(t("[4/4] Sin --unity: no se exporta", "[4/4] No --unity: nothing exported"))
    print(t("Listo en {s:.0f} s", "Done in {s:.0f} s", s=time.time() - t0))
    return 0


def detectar_antialias(ply_path):
    """LichtFeld / Splat Studio ponen el modo en el nombre (…-antialias-…, --enable-mip)."""
    name = os.path.basename(ply_path).lower()
    return "antialias" in name or "mip" in name.replace("mcmc", "")


def _rotate(q, v):
    """Rota v con el cuaternión q = (x, y, z, w)."""
    x, y, z, w = q
    u = np.array([x, y, z])
    v = np.asarray(v, np.float64)
    return v + 2 * np.cross(u, np.cross(u, v) + w * v)


def camara_inicio(man, inicio, altura, keep_pitch=False):
    """
    Pose de cámara en ejes de Unity (rotación de nivelación aplicada): la cámara COLMAP `inicio` = (centro, adelante)
    si la hay; si no, el centro del suelo a `altura` m mirando a +z.
    """
    q = man["rotacion_unity"]
    if inicio is None:
        return {"pos": [0.0, float(altura), 0.0], "adelante": [0.0, 0.0, 1.0]}
    c, f = inicio
    tr = np.asarray(man["traslacion_restada"])
    s = man.get("escala_aplicada", 1.0)
    rdf = np.array([1.0, -1.0, 1.0])                 # archivo -> Unity al importar en RDF
    pos = _rotate(q, (np.asarray(c) - tr) * s * rdf)
    fwd = _rotate(q, np.asarray(f) * rdf)
    if not keep_pitch:
        fwd[1] = 0.0
    fwd /= max(np.linalg.norm(fwd), 1e-9)
    return {"pos": [float(v) for v in pos], "adelante": [float(v) for v in fwd]}


# --------------------------------------------------------------------------------------- export Unity

PLY_META = """fileFormatVersion: 2
guid: {guid}
ScriptedImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 2
  userData:
  assetBundleName:
  assetBundleVariant:
  script: {{fileID: 11500000, guid: 7468ea6559404cbc8f3e83b0b1a00683, type: 3}}
  Compression: 1
  OpacityPruneThreshold: 0
  SourceCoordinates: 6
"""
TEXT_META = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def _unity_dst(unity_project, nombre, carpeta=None):
    dst = os.path.join(unity_project, *(carpeta or UNITY_DIR_DEFAULT).replace("\\", "/").split("/"), nombre)
    os.makedirs(dst, exist_ok=True)
    return dst


def _write_meta(target, tpl):
    import uuid
    if not os.path.exists(target + ".meta"):
        with open(target + ".meta", "w", encoding="utf-8", newline="\n") as f:
            f.write(tpl.format(guid=uuid.uuid4().hex))


def exportar_unity(unity_project, nombre, plys, licencia, carpeta=None):
    """
    Copia las pirámides (PLY + manifiesto) a <proyecto>/<carpeta>/<nombre>/ con sus .meta ya escritos (importador de
    SplatKit en RDF: se importan bien a la primera).
    """
    import shutil
    dst = _unity_dst(unity_project, nombre, carpeta)
    for ply_path in plys:
        for src, tpl in ((ply_path, PLY_META), (ply_path[:-4] + ".json", TEXT_META)):
            target = os.path.join(dst, os.path.basename(src))
            shutil.copyfile(src, target)
            _write_meta(target, tpl)
            print(f"  {target}")
    if licencia:
        target = os.path.join(dst, "LICENCIA.txt")
        with open(target, "w", encoding="utf-8") as f:
            f.write(licencia + "\n")
        _write_meta(target, TEXT_META)


def exportar_texto(unity_project, nombre, path, carpeta=None):
    """Copia un JSON (manifiesto, colisiones) a <proyecto>/<carpeta>/<nombre>/ con su .meta de texto."""
    import shutil
    target = os.path.join(_unity_dst(unity_project, nombre, carpeta), os.path.basename(path))
    shutil.copyfile(path, target)
    _write_meta(target, TEXT_META)
    print(f"  {target}")


def cmd_colision(a):
    """Colisiones del entorno: <escena>/colision.json + colision_planta.png."""
    from splatkit import colision
    t0 = time.time()
    ply = SplatPly.open(a.ply)
    o = colision.Opciones(voxel=a.voxel, min_alfa=a.alfa, min_splats=a.min_splats)
    rot = None
    if os.path.exists(a.ply[:-4] + ".json"):
        rot = _load(a.ply[:-4] + ".json").get("rotacion_unity")
    man, occ, origin = colision.build(ply, o, rotacion=rot)
    base = entorno_dir(a.ply, None, a)
    out = os.path.join(base, "colision.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(man, f, ensure_ascii=False)
    colision.vista_planta(occ, origin, o.voxel, os.path.join(base, "colision_planta.png"))
    print(t("{o}: {c:,} cajas ({s:.0f} s)", "{o}: {c:,} boxes ({s:.0f} s)", o=out, c=len(man["cajas"]) // 6,
            s=time.time() - t0))
    if a.unity:
        exportar_texto(a.unity, a.nombre, out, a.unity_carpeta)
    return 0


# -------------------------------------------------------------------------------------------- comparar

def ssim(x, y):
    from scipy.ndimage import gaussian_filter
    x, y = x.mean(2), y.mean(2)
    mx, my = gaussian_filter(x, 1.5), gaussian_filter(y, 1.5)
    vx = gaussian_filter(x * x, 1.5) - mx * mx
    vy = gaussian_filter(y * y, 1.5) - my * my
    cxy = gaussian_filter(x * y, 1.5) - mx * my
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    return float((((2 * mx * my + c1) * (2 * cxy + c2)) / ((mx * mx + my * my + c1) * (vx + vy + c2))).mean())


def cmd_comparar(a):
    """PSNR/SSIM de cada variante contra la referencia, por vista, y hojas lado a lado."""
    from PIL import Image

    def load(p):
        return np.asarray(Image.open(p).convert("RGB"), np.float32) / 255.0

    ref_dir = os.path.join(a.bench, a.referencia)
    labels = a.variantes.split(",")
    table = {}
    views = sorted(f[:-4] for f in os.listdir(ref_dir) if f.endswith(".png"))

    def times_of(d):
        tj = os.path.join(d, "tiempos.json")
        if not os.path.exists(tj):
            return {}
        return {v["vista"]: v["ms_mediana"] for v in _load(tj)["vistas"]}

    for lab in labels:
        times = times_of(os.path.join(a.bench, lab))
        table[lab] = {}
        for v in views:
            p = os.path.join(a.bench, lab, v + ".png")
            if not os.path.exists(p):
                continue
            r, x = load(os.path.join(ref_dir, v + ".png")), load(p)
            if a.centro < 1:
                h, w = r.shape[:2]
                ch, cw = int(h * a.centro / 2), int(w * a.centro / 2)
                r, x = r[h // 2 - ch:h // 2 + ch, w // 2 - cw:w // 2 + cw], x[h // 2 - ch:h // 2 + ch, w // 2 - cw:w // 2 + cw]
            mse = float(((r - x) ** 2).mean())
            psnr = 10 * np.log10(1.0 / max(mse, 1e-12))
            table[lab][v] = {"psnr": psnr, "ssim": ssim(r, x), "ms": times.get(v)}
            if a.hojas:
                diff = np.clip(np.abs(r - x) * 4, 0, 1)
                sheet = np.concatenate([r, x, diff], 1)
                h, w = sheet.shape[:2]
                img = Image.fromarray((sheet * 255).astype(np.uint8)).resize((w // 2, h // 2))
                os.makedirs(os.path.join(a.bench, "comparar"), exist_ok=True)
                img.save(os.path.join(a.bench, "comparar", f"{v}__{a.referencia}_vs_{lab}.jpg"), quality=88)
    ref_times = times_of(ref_dir)
    print(t("Referencia: {r}", "Reference: {r}", r=a.referencia))
    print(t("| vista | ref ms | ", "| view | ref ms | ") + " | ".join(f"{l} ms / PSNR / SSIM" for l in labels) + " |")
    print("|---|---|" + "---|" * len(labels))
    for v in views:
        cells = []
        for l in labels:
            m = table[l].get(v)
            cells.append("—" if m is None else f"{m['ms'] or 0:.1f} / {m['psnr']:.1f} / {m['ssim']:.3f}")
        rt = ref_times.get(v)
        print(f"| {v} | {rt:.1f} | " if rt else f"| {v} | — | ", end="")
        print(" | ".join(cells) + " |")
    summary = {}
    for l in labels:
        rows = list(table[l].values())
        if rows:
            summary[l] = {"psnr_media": float(np.mean([r["psnr"] for r in rows])),
                          "psnr_peor": float(np.min([r["psnr"] for r in rows])),
                          "ssim_media": float(np.mean([r["ssim"] for r in rows])),
                          "ms_mediana": float(np.median([r["ms"] for r in rows if r["ms"] is not None] or [0]))}
            print(t("  {l}: PSNR medio {p:.2f} dB (peor {w:.2f}) · SSIM {s:.3f} · {ms:.1f} ms",
                    "  {l}: mean PSNR {p:.2f} dB (worst {w:.2f}) · SSIM {s:.3f} · {ms:.1f} ms",
                    l=l, p=summary[l]["psnr_media"], w=summary[l]["psnr_peor"], s=summary[l]["ssim_media"],
                    ms=summary[l]["ms_mediana"]))
    out = os.path.join(a.bench, f"comparar_{a.referencia}" + (f"_centro{a.centro:g}" if a.centro < 1 else "") + ".json")
    _dump(out, {"referencia": a.referencia, "tiempos_ref": ref_times, "variantes": table, "resumen": summary})
    return 0


def cmd_recortar(a):
    """Recorta objetos de la escena (cajas en ejes de Unity) a PLY sueltos con el pivote en la base."""
    from splatkit import recorte
    objetos = _load(a.objetos)
    objetos = objetos.get("objetos", objetos) if isinstance(objetos, dict) else objetos
    out = a.salida or os.path.join(entorno_dir(a.ply, None, a), "recortes")
    fichas = recorte.recortar_lista(a.ply, objetos, out, recorte.Opciones(max_quest=a.max_quest))
    for k, f in fichas.items():
        print(t("  {k}: {n:,} splats, pivote {p}", "  {k}: {n:,} splats, pivot {p}", k=k, n=f["splats"], p=f["pivote"]))
    print(t("Recortes en {o}", "Cut-outs in {o}", o=out))
    return 0


def cmd_perfiles(a):
    if a.json:
        print(json.dumps(profiles.load(), indent=2, ensure_ascii=False))
    else:
        print(profiles.describe())
    return 0


# ---------------------------------------------------------------------------------------------- común

def leer_licencia(ply_path):
    p = os.path.join(os.path.dirname(os.path.abspath(ply_path)), "license.txt")
    if os.path.exists(p):
        return open(p, encoding="utf-8").read().strip()
    man = ply_path[:-4] + ".json"                   # PLY derivado (limpio_shN): la licencia va en su manifiesto
    if os.path.exists(man):
        return _load(man).get("licencia")
    return None


def print_summary(r):
    n = r["splats"]
    pc = lambda k: f"{100 * k / n:.1f} %"  # noqa: E731
    z = r["zona_principal"]
    print(t("\nZona principal (p0,5-p99,5): {s} · diagonal {d:.2f}", "\nMain zone (p0.5-p99.5): {s} · diagonal {d:.2f}",
            s=np.round(z["tamano"], 2).tolist(), d=z["diagonal"]))
    print(t("No finitos: {a} · fuera de la zona (+10 %): {b:,} ({c})", "Non-finite: {a} · outside the zone (+10 %): {b:,} ({c})",
            a=r["no_finitos"], b=r["fuera_de_zona_10pct"], c=pc(r["fuera_de_zona_10pct"])))
    o = r["opacidad"]["bajo"]
    print(t("Opacidad baja: ", "Low opacity: ") + " · ".join(f"<{k}: {pc(v)}" for k, v in o.items()))
    s = r["tamano_sigma_max"]["relativo_diagonal"]
    print(t("Splats grandes (σ máx > % diagonal): ", "Large splats (max σ > % of diagonal): ")
          + " · ".join(f">{float(k) * 100:g} %: {v:,}" for k, v in s.items()))
    f = r["relleno"]
    print(t("Relleno (área×alfa) del 1 % más grande: {a:.0f} % · 5 %: {b:.0f} % · 10 %: {c:.0f} %",
            "Fill (area×alpha) of the largest 1 %: {a:.0f} % · 5 %: {b:.0f} % · 10 %: {c:.0f} %",
            a=100 * f["cuota_1pct_mas_grandes"], b=100 * f["cuota_5pct"], c=100 * f["cuota_10pct"]))
    if r["sh"]["bajo_umbral"]:
        print(t("SH: {mb:.0f} MB · amplitud mediana {m:.3f} · ", "SH: {mb:.0f} MB · median amplitude {m:.3f} · ",
                mb=r["sh"]["memoria_sh_mb"], m=r["sh"]["amplitud_percentiles"]["50"])
              + " · ".join(f"<{k}: {pc(v)}" for k, v in r["sh"]["bajo_umbral"].items()))
    d = r["densidad"]
    print(t("Densidad (vóxel {v:.3f}, {o:,} ocupados): aislados ", "Density (voxel {v:.3f}, {o:,} occupied): isolated ",
            v=d["voxel"], o=d["voxeles_ocupados"])
          + " · ".join(t("<{k} vecinos: {p}", "<{k} neighbours: {p}", k=k, p=pc(v)) for k, v in d["aislados"].items()))
    print(t("Eje arriba (candidatos): ", "Up axis (candidates): ")
          + " · ".join(f"{c['arriba']}{c['eje']} (pico {c['pico']:.1f})" for c in r["eje_arriba"]))
    s = r.get("suelo")
    if s and "eje" in s:
        print(t("Suelo: arriba {e}, normal {n}, inclinación {i:.2f}°, punto {p} ({k:,} de {m:,} splats en el plano)",
                "Floor: up {e}, normal {n}, tilt {i:.2f}°, point {p} ({k:,} of {m:,} splats on the plane)",
                e=s["eje"], n=np.round(s["normal_arriba"], 4).tolist(), i=s["inclinacion_grados"],
                p=np.round(s["punto"], 3).tolist(), k=s["inliers"], m=s["muestra"]))


def build_parser():
    ap = argparse.ArgumentParser(prog="splatkit", description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lang", choices=["es", "en"], help=t("idioma de los mensajes", "message language"))
    ap.add_argument("--espacio", help=t("carpeta donde van las escenas (SPLATKIT_WORKSPACE)",
                                        "folder where scenes are written (SPLATKIT_WORKSPACE)"))
    sub = ap.add_subparsers(dest="cmd", required=True)

    def unity_args(p):
        p.add_argument("--unity", help=t("ruta del proyecto Unity al que exportar", "Unity project to export to"))
        p.add_argument("--unity-carpeta", default=UNITY_DIR_DEFAULT,
                       help=t("carpeta del proyecto Unity para las escenas (por defecto {d})",
                              "Unity project folder for scenes (default {d})", d=UNITY_DIR_DEFAULT))

    p = sub.add_parser("info", help=t("diagnóstico + vistas previas", "diagnostics + previews"))
    p.add_argument("ply")
    p.add_argument("--nombre", help=t("nombre de la escena (carpeta en el espacio de trabajo)", "scene name (workspace folder)"))
    p.add_argument("--salida", help=t("carpeta del informe (por defecto <escena>/analisis)", "report folder (default <scene>/analisis)"))
    p.add_argument("--res", type=int, default=900, help=t("lado de las vistas previas en píxeles", "preview side in pixels"))
    p.add_argument("--sin-vistas", action="store_true", help=t("no generar vistas previas", "skip previews"))
    p.set_defaults(func=cmd_info)

    p = sub.add_parser("camaras", help=t("cámaras de prueba para el banco de Unity", "test cameras for the Unity bench"))
    p.add_argument("ply", help=t("escena (limpia o pirámide con su .json para --lichtfeld)",
                                 "scene (clean output or pyramid with its .json for --lichtfeld)"))
    p.add_argument("--ojos", type=float, default=1.6, help=t("altura de los ojos sobre el suelo (m)", "eye height above the floor (m)"))
    p.add_argument("--fov", type=float, help=t("campo de visión vertical (grados)", "vertical field of view (degrees)"))
    p.add_argument("--recorrido", type=int, default=0,
                   help=t("N puntos repartidos por todo el suelo (camaras_recorrido.json)",
                          "N points spread over the whole floor (camaras_recorrido.json)"))
    p.add_argument("--lichtfeld", help=t("convertir un camino de cámara de LichtFeld Studio (.json)",
                                         "convert a LichtFeld Studio camera path (.json)"))
    p.add_argument("--max-vistas", type=int, default=12, help=t("vistas tomadas del camino", "views taken from the path"))
    p.add_argument("--semilla", type=int, default=0)
    p.set_defaults(func=cmd_camaras)

    p = sub.add_parser("limpiar", help=t("quitar flotantes/transparentes/gigantes, recortar SH y trasladar",
                                         "remove floaters/transparent/huge splats, trim SH and translate"))
    p.add_argument("ply")
    p.add_argument("--nombre", default="limpio", help=t("prefijo de la salida (<escena>/<nombre>_shN.ply)", "output prefix"))
    p.add_argument("--sh", type=int, default=3, help=t("grado SH de salida", "output SH degree"))
    p.add_argument("--variantes-sh", help=t("varias salidas a la vez, p. ej. 3,1,0", "several outputs at once, e.g. 3,1,0"))
    p.add_argument("--opacidad", type=float, default=0.01, help=t("quitar alfa menor que esto", "remove alpha below this"))
    p.add_argument("--vecinos", type=int, default=4, help=t("quitar splats con menos vecinos (flotantes)", "remove splats with fewer neighbours (floaters)"))
    p.add_argument("--gigante", type=float, default=0.02, help=t("quitar σ máx mayor que esta fracción de la diagonal", "remove max σ above this fraction of the diagonal"))
    p.add_argument("--margen", type=float, default=0.10, help=t("margen fuera de la zona principal", "margin outside the main zone"))
    p.set_defaults(func=cmd_limpiar)

    for name, func, hlp in (("estacion", cmd_estacion, t("poda por importancia desde una estación", "importance pruning from a station")),
                            ("lod", cmd_lod, t("LOD por fusión de splats desde una estación", "merge-based LOD from a station"))):
        p = sub.add_parser(name, help=hlp)
        p.add_argument("ply", help=t("salida de 'limpiar' (con su .json)", "output of 'limpiar' (with its .json)"))
        p.add_argument("--nombre", required=True)
        g = p.add_mutually_exclusive_group(required=True)
        g.add_argument("--vista", help=t("centro = posición de esta vista de camaras.json", "centre = position of this view in camaras.json"))
        g.add_argument("--centro", help=t("centro 'x,z' en coordenadas de Unity", "centre 'x,z' in Unity coordinates"))
        p.add_argument("--radio", type=float, default=0.75, help=t("radio de la zona de movimiento (m)", "movement zone radius (m)"))
        if name == "estacion":
            p.add_argument("--k", default="100,150,300", help=t("miles de splats a conservar (varios)", "thousands of splats to keep (several)"))
            p.add_argument("--distancia", type=float, help=t("ignorar splats más lejos que esto (m)", "ignore splats farther than this (m)"))
            p.add_argument("--sh", type=int, default=3)
        else:
            p.add_argument("--angulos", default="0.3,0.5,0.8", help=t("lado angular de las celdas en grados (varios)", "angular cell size in degrees (several)"))
            p.add_argument("--ojos", type=float, default=1.6, help=t("altura del centro de la rejilla (m)", "grid centre height (m)"))
            p.add_argument("--inflar", type=float, default=0.0, help=t("ensancha cada fusionada (fracción de celda)", "widen each merged splat (cell fraction)"))
            p.add_argument("--radial", type=float, default=1.0, help=t("largo de las celdas a lo largo del rayo (× lado)", "cell length along the ray (× side)"))
            p.add_argument("--sin-oclusion", action="store_true", help=t("no descartar lo que no se ve desde la estación", "keep splats hidden from the station"))
            p.add_argument("--sh", type=int, default=1)
        p.set_defaults(func=func)

    p = sub.add_parser("piramide", help=t("pirámide LOD por trozos de toda la escena", "chunked LOD pyramid of the whole scene"))
    p.add_argument("ply", help=t("escena limpia (con su .json)", "clean scene (with its .json)"))
    p.add_argument("--nombre", default="piramide")
    p.add_argument("--trozo", type=float, default=3.0, help=t("lado de los trozos (m)", "chunk side (m)"))
    p.add_argument("--voxeles", default="0.04,0.08,0.16,0.32,0.64,1.28", help=t("vóxel de cada nivel (m)", "voxel of each level (m)"))
    p.add_argument("--nivel-minimo", type=int, default=0, help=t("omitir los niveles más finos (Quest: 1)", "skip the finest levels (Quest: 1)"))
    p.add_argument("--sh", type=int, default=1)
    p.set_defaults(func=cmd_piramide)

    p = sub.add_parser("preparar", help=t("cadena completa para una escena nueva (limpieza + pirámides + Unity)",
                                          "full pipeline for a new scene (cleanup + pyramids + Unity)"))
    p.add_argument("ply", help=t("escena 3DGS original (.ply)", "original 3DGS scene (.ply)"))
    p.add_argument("--nombre", required=True, help=t("nombre de la escena", "scene name"))
    unity_args(p)
    p.add_argument("--perfiles", help=t("perfiles a preparar, separados por comas (por defecto todos; ver 'perfiles')",
                                        "profiles to prepare, comma separated (default all; see 'perfiles')"))
    p.add_argument("--trozo", type=float, help=t("lado de los trozos (m); por defecto diagonal/20 entre 1 y 3 m",
                                                 "chunk side (m); default diagonal/20 clamped to 1-3 m"))
    p.add_argument("--sh", type=int, default=1, help=t("grado SH máximo de la escena limpia", "max SH degree of the clean scene"))
    p.add_argument("--piramides", help=t("calidades a exportar, separadas por comas: low (Low detail), high (High "
                                         "detail), max (Original, máxima calidad). Por defecto low,high",
                                         "qualities to export, comma separated: low (Low detail), high (High detail), "
                                         "max (Original, maximum quality). Default low,high"))
    p.add_argument("--nivel-bajo", "--nivel-quest", dest="nivel_bajo", type=int,
                   help=t("nivel mínimo de Low detail (por defecto 1)", "min level of Low detail (default 1)"))
    p.add_argument("--agujas", type=float, default=10.0,
                   help=t("quitar agujas (σ1/σ2 mayor que esto, > 3 cm y alfa < 0,35); 0 = no filtrar",
                          "remove needles (σ1/σ2 above this, > 3 cm and alpha < 0.35); 0 = off"))
    p.add_argument("--vecinos", type=int, default=16, help=t("quitar splats con menos vecinos", "remove splats with fewer neighbours"))
    p.add_argument("--colmap", help=t("carpeta sparse/0 (o images.txt) de COLMAP: suelo, escala y cámara inicial",
                                      "COLMAP sparse/0 folder (or images.txt): floor, scale and initial camera"))
    p.add_argument("--altura-camara", type=float, default=1.5, help=t("altura de la cámara al grabar (m), para la escala",
                                                                      "camera height while recording (m), for the scale"))
    p.add_argument("--escala", type=float, help=t("metros por unidad (anula la estimada)", "metres per unit (overrides the estimate)"))
    p.add_argument("--gigante", type=float, default=0.05, help=t("quitar σ máx mayor que esta fracción de la diagonal",
                                                                 "remove max σ above this fraction of the diagonal"))
    g = p.add_mutually_exclusive_group()
    g.add_argument("--antialias", dest="antialias", action="store_true", default=None,
                   help=t("la escena se entrenó con antialias (por defecto se deduce del nombre)",
                          "the scene was trained with antialiasing (default: guessed from the file name)"))
    g.add_argument("--sin-antialias", dest="antialias", action="store_false")
    p.set_defaults(func=cmd_preparar)

    p = sub.add_parser("colision", help=t("cajas de colisión del entorno para Unity", "environment collision boxes for Unity"))
    p.add_argument("ply", help=t("escena limpia: mismo marco que las pirámides", "clean scene: same frame as the pyramids"))
    p.add_argument("--voxel", type=float, default=0.2, help=t("lado de los vóxeles (m)", "voxel side (m)"))
    p.add_argument("--alfa", type=float, default=0.3, help=t("opacidad mínima de los splats que cuentan", "min opacity of counted splats"))
    p.add_argument("--min-splats", type=int, default=5, help=t("splats por vóxel para ocuparlo", "splats per voxel to occupy it"))
    p.add_argument("--nombre", default="escena", help=t("carpeta de la escena en Unity", "scene folder in Unity"))
    unity_args(p)
    p.set_defaults(func=cmd_colision)

    p = sub.add_parser("comparar", help=t("PSNR/SSIM y tiempos de variantes del banco de Unity", "PSNR/SSIM and timings of Unity bench variants"))
    p.add_argument("bench", help=t("carpeta del banco", "bench folder"))
    p.add_argument("--referencia", required=True)
    p.add_argument("--variantes", required=True, help=t("etiquetas separadas por comas", "comma separated labels"))
    p.add_argument("--hojas", action="store_true", help=t("guardar hojas referencia | variante | diferencia×4", "save reference | variant | diff×4 sheets"))
    p.add_argument("--centro", type=float, default=1.0, help=t("medir solo la zona central (fracción)", "measure only the central zone (fraction)"))
    p.set_defaults(func=cmd_comparar)

    p = sub.add_parser("recortar", help=t("recortar objetos de la escena a PLY sueltos (pivote en la base)",
                                          "cut objects out of the scene into separate PLYs (pivot at the base)"))
    p.add_argument("ply", help=t("escena limpia (ejes RDF; cajas en ejes de Unity)", "clean scene (RDF; boxes in Unity axes)"))
    p.add_argument("--objetos", required=True, help=t("JSON con [{id, caja: {min, max}, excluir?, margen?}]",
                                                      "JSON with [{id, caja: {min, max}, excluir?, margen?}]"))
    p.add_argument("--salida", help=t("carpeta de salida (por defecto <escena>/recortes)", "output folder"))
    p.add_argument("--max-quest", type=int, default=150_000,
                   help=t("por encima, se escribe también <id>_quest.ply ligero", "above this, also write a light <id>_quest.ply"))
    p.set_defaults(func=cmd_recortar)

    p = sub.add_parser("perfiles", help=t("lista los perfiles de calidad", "list the quality profiles"))
    p.add_argument("--json", action="store_true")
    p.set_defaults(func=cmd_perfiles)
    return ap


def main(argv=None, extra=None):
    """``extra(sub)`` permite que otra herramienta añada subcomandos (p. ej. una app que reutiliza el CLI)."""
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # la consola de Windows es cp1252
    argv = list(sys.argv[1:] if argv is None else argv)
    if "--lang" in argv:                                             # el idioma antes de construir la ayuda
        i = argv.index("--lang")
        if i + 1 < len(argv):
            set_lang(argv[i + 1])
    ap = build_parser()
    if extra:
        extra(next(a for a in ap._actions if isinstance(a, argparse._SubParsersAction)))
    a = ap.parse_args(argv)
    if a.lang:
        set_lang(a.lang)
    return a.func(a)


if __name__ == "__main__":
    sys.exit(main())
