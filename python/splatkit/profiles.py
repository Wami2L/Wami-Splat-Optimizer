"""
Perfiles de calidad (``data/profiles.json``) / Quality profiles.

Un perfil tiene dos partes: ``bake`` (qué pirámide genera la cadena offline) y ``runtime`` (presupuesto de splats,
resolución del pase de splats, SH, orden, LOD…), que usa el renderizador de Unity con el mismo archivo.
"""

import json
import os

from .i18n import pick, t

PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "profiles.json")
_cache = None


def load(path=None):
    global _cache
    if path:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    if _cache is None:
        with open(PATH, encoding="utf-8") as f:
            _cache = json.load(f)
    return _cache


def ids(data=None):
    return [p["id"] for p in (data or load())["profiles"]]


def get(profile_id, data=None):
    data = data or load()
    for p in data["profiles"]:
        if p["id"] == profile_id:
            return p
    raise KeyError(t("perfil desconocido «{p}» (hay: {l})", "unknown profile '{p}' (available: {l})",
                     p=profile_id, l=", ".join(ids(data))))


def pyramid(pyramid_id, data=None):
    data = data or load()
    for p in data["pyramids"]:
        if p["id"] == pyramid_id:
            return p
    raise KeyError(t("pirámide desconocida «{p}»", "unknown pyramid '{p}'", p=pyramid_id))


def pyramids_for(profile_ids=None, data=None):
    """Pirámides que hay que generar para estos perfiles (todas si es None), en el orden del archivo."""
    data = data or load()
    wanted = {get(i, data)["bake"]["pyramid"] for i in (profile_ids or ids(data))}
    return [p for p in data["pyramids"] if p["id"] in wanted]


def describe(data=None):
    """Tabla de texto de los perfiles en el idioma actual."""
    data = data or load()
    lines = []
    for p in data["profiles"]:
        r = p["runtime"]
        budget = t("sin límite", "unlimited") if not r["splat_budget"] else f"{r['splat_budget']:,}"
        lines.append(f"{p['id']:<10} {pick(p['label'])}")
        lines.append("           " + t(
            "splats {b} · pase {res:.0%} · SH{sh} · orden {s}/{bits} bits · pirámide {pyr} · {hz}",
            "splats {b} · pass {res:.0%} · SH{sh} · sort {s}/{bits} bits · pyramid {pyr} · {hz}",
            b=budget, res=r["splat_resolution"], sh=r["sh_degree"], s=r["sort_mode"], bits=r["sort_key_bits"],
            pyr=p["bake"]["pyramid"], hz=f"{r['target_hz']} Hz" if r["target_hz"] else "—"))
        lines.append("           " + pick(p["description"]))
    return "\n".join(lines)
