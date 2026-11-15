"""
Recortes de objetos de una escena 3DGS / Object cut-outs from a 3DGS scene.

Por objeto, una caja en ejes de Unity del PLY limpio (x, -y, z del archivo RDF: el mismo marco local que las pirámides y
colision.json). El recorte se queda con lo de dentro de la caja (menos las cajas de `excluir`), sin casi
transparentes, gigantes ni sueltos, y se escribe con el pivote en el centro de su base. Si pasa de `max_quest` splats
se escribe además una variante ligera con los más importantes.

Per object, a box in Unity axes of the clean PLY. The cut keeps what is inside (minus `excluir` boxes), drops
near-transparent, huge and isolated splats, and is written with the pivot at the centre of its base.
"""

import json
import os
from dataclasses import dataclass

import numpy as np

from .analisis import neighbourhood_counts
from .i18n import t
from .ply import SplatPly, sigmoid, write_ply

_RDF = np.array([1.0, -1.0, 1.0], np.float32)       # archivo RDF <-> ejes de Unity (involutivo)


@dataclass
class Opciones:
    opacidad_min: float = 0.02      # fuera los casi transparentes (en la sala se ven contra un fondo liso)
    vecinos_min: int = 3            # fuera los sueltos (en el cubo 3×3×3 de vóxeles)
    voxel: float = 0.08             # m, para contar vecinos (los splats de zonas poco vistas son grandes y ralos)
    margen: float = 0.0             # m que se añaden a cada lado de la caja
    max_quest: int = 150_000        # splats de la variante de Quest 2 (<id>_quest.ply) si el recorte pasa de esto
    gigante: float = 0.04           # fuera los splats con σ mayor que esta fracción de la diagonal de la caja: son
                                    # manchas de pared o de fondo que en la escena quedan detrás y en la sala, delante


def _caja(d, margen=0.0):
    lo = np.asarray(d["min"], np.float32) - margen
    hi = np.asarray(d["max"], np.float32) + margen
    if np.any(hi <= lo):
        raise ValueError(t("caja vacía: {d}", "empty box: {d}", d=d))
    return lo, hi


def _dentro(pos, caja):
    lo, hi = caja
    return np.all((pos >= lo) & (pos <= hi), axis=1)


@dataclass
class Splats:
    """Lo que hace falta de cada splat para recortar: centro (ejes de Unity), opacidad, σ mayor e importancia
    (opacidad × área de las dos σ mayores)."""
    pos: np.ndarray
    alpha: np.ndarray
    sigma: np.ndarray
    importancia: np.ndarray


def leer_splats(ply: SplatPly) -> Splats:
    pos = ply.columns(["x", "y", "z"]) * _RDF
    alpha = sigmoid(ply.columns(["opacity"])[:, 0])
    ls = np.sort(ply.columns(["scale_0", "scale_1", "scale_2"]), axis=1)
    return Splats(pos, alpha, np.exp(ls[:, 2]), alpha * np.exp(ls[:, 1] + ls[:, 2]))


def mascara_objeto(s: Splats, obj, o: Opciones):
    """Splats del objeto: dentro de la caja, fuera de las cajas de `excluir`, opacos, no gigantes y no aislados."""
    pos = s.pos
    caja = _caja(obj["caja"], obj.get("margen", o.margen))
    diag = float(np.linalg.norm(caja[1] - caja[0]))
    m = (_dentro(pos, caja) & (s.alpha >= obj.get("opacidad_min", o.opacidad_min)) &
         (s.sigma <= obj.get("gigante", o.gigante) * diag))
    for ex in obj.get("excluir", []):
        m &= ~_dentro(pos, _caja(ex))
    idx = np.nonzero(m)[0]
    if len(idx) and o.vecinos_min > 0:
        cnt, _ = neighbourhood_counts(pos[idx], o.voxel, caja[0])
        m[idx[cnt < o.vecinos_min]] = False
    return m


def recortar(ply: SplatPly, s: Splats, obj, out_path, o: Opciones):
    """
    Escribe el PLY del objeto (RDF) con el pivote en el centro de la base de lo recortado y devuelve su ficha:
    pivote (ejes de Unity del entorno), caja ajustada (relativa al pivote) y número de splats. Si pasa de
    `o.max_quest` splats escribe además <id>_quest.ply con los más importantes (en Quest 2 la sala de inspección
    no puede dibujar cientos de miles).
    """
    m = mascara_objeto(s, obj, o)
    n = int(m.sum())
    if n == 0:
        raise ValueError(t("{i}: la caja no contiene splats", "{i}: the box contains no splats", i=obj.get("id")))
    p = s.pos[m]
    lo, hi = np.percentile(p, [0.5, 99.5], axis=0)
    pivote = np.array([(lo[0] + hi[0]) / 2, lo[1], (lo[2] + hi[2]) / 2], np.float32)
    piv_rdf = pivote * _RDF

    def trasladar(cols):
        cols["x"] = cols["x"] - piv_rdf[0]
        cols["y"] = cols["y"] - piv_rdf[1]
        cols["z"] = cols["z"] - piv_rdf[2]
        return cols

    nota = f"recorte {obj.get('id')} de {os.path.basename(ply.path)}; pivote (Unity) {pivote.round(4).tolist()}"
    write_ply(out_path, ply, mask=m, transform=trasladar, comments=[nota])
    ficha = {"pivote": pivote.round(4).tolist(), "min": (lo - pivote).round(4).tolist(),
             "max": (hi - pivote).round(4).tolist(), "splats": n}
    quest = out_path[:-4] + "_quest.ply"
    if n > o.max_quest:
        idx = np.nonzero(m)[0]
        mq = np.zeros_like(m)
        mq[idx[np.argsort(-s.importancia[idx])[:o.max_quest]]] = True
        write_ply(quest, ply, mask=mq, transform=trasladar, comments=[nota + "; variante Quest 2"])
        ficha["ply_quest"] = os.path.basename(quest)
    elif os.path.exists(quest):
        os.remove(quest)
    return ficha


def recortar_lista(ply_path, objetos, out_dir, o=None):
    """Recorta todos los objetos ({id, caja{min,max}, excluir?, margen?}) a out_dir/<id>.ply y devuelve las fichas."""
    o = o or Opciones()
    os.makedirs(out_dir, exist_ok=True)
    ply = SplatPly.open(ply_path)
    s = leer_splats(ply)
    fichas = {}
    for obj in objetos:
        fichas[obj["id"]] = recortar(ply, s, obj, os.path.join(out_dir, obj["id"] + ".ply"), o)
    with open(os.path.join(out_dir, "recortes.json"), "w", encoding="utf-8") as f:
        json.dump({"fuente": ply_path, "objetos": fichas}, f, indent=1, ensure_ascii=False)
    return fichas
