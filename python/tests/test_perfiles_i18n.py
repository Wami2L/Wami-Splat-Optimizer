"""Perfiles de calidad compartidos y mensajes en dos idiomas."""

import json
import os

import pytest

from splatkit import i18n, profiles

UNITY_COPY = os.path.join(os.path.dirname(__file__), "..", "..", "unity", "com.wami.splatoptimizer", "Runtime", "Resources",
                          "SplatKitProfiles.json")

RUNTIME_KEYS = {"splat_budget", "lod_angle_deg", "sh_degree", "splat_resolution", "depth_prepass", "sort_mode",
                "sort_key_bits", "stable_xr_lod", "behind_penalty", "min_pixel_size", "min_contribution",
                "periphery", "target_hz", "adaptive"}


def test_perfiles_completos_y_bilingues():
    data = profiles.load()
    pyr = {p["id"] for p in data["pyramids"]}
    assert data["default"] in profiles.ids()
    for p in data["profiles"]:
        assert set(p["label"]) == {"es", "en"} and set(p["description"]) == {"es", "en"}
        assert p["bake"]["pyramid"] in pyr
        assert set(p["runtime"]) == RUNTIME_KEYS, p["id"]
        r = p["runtime"]
        assert 0.25 <= r["splat_resolution"] <= 1.0
        assert r["sort_mode"] in ("radial", "view") and r["sort_key_bits"] in (16, 32)


def test_piramides_por_perfil():
    assert [p["id"] for p in profiles.pyramids_for(["quest2"])] == ["quest"]
    assert [p["id"] for p in profiles.pyramids_for(["quest2", "pcvr_low"])] == ["full", "quest"]
    assert len(profiles.pyramids_for()) == len(profiles.load()["pyramids"])
    with pytest.raises(KeyError):
        profiles.get("no_existe")


def test_copia_de_unity_identica():
    if not os.path.exists(UNITY_COPY):
        pytest.skip("paquete de Unity no presente")
    with open(UNITY_COPY, encoding="utf-8") as f:
        assert json.load(f) == profiles.load()


def test_idiomas():
    try:
        i18n.set_lang("es")
        assert i18n.t("Hola {n}", "Hello {n}", n=1) == "Hola 1"
        assert i18n.pick({"es": "sí", "en": "yes"}) == "sí"
        assert "splats" in profiles.describe()
        i18n.set_lang("en")
        assert i18n.t("Hola {n}", "Hello {n}", n=1) == "Hello 1"
        assert "Automatic" in profiles.describe()
        with pytest.raises(ValueError):
            i18n.set_lang("fr")
    finally:
        i18n.set_lang(None)


def test_lichtfeld_a_unity(tmp_path):
    """Un camino de LichtFeld (mundo = diag(1,-1,-1)·COLMAP) mirando a +z de COLMAP se convierte en una vista de
    Unity con la escena sin rotar: RDF -> Unity invierte y, así que mirar a +z COLMAP es mirar a +z en Unity."""
    from splatkit.cli import lichtfeld_to_unity
    path = tmp_path / "camino.json"
    # Cámara en COLMAP (1, 2, 3) mirando a +z: R_c2w = identidad -> R_visor = D·I·D = I -> q = (1, 0, 0, 0).
    path.write_text(json.dumps({"keyframes": [{"position": [1.0, -2.0, -3.0], "rotation": [1, 0, 0, 0],
                                               "focal_length_mm": 12.0}]}))
    man = {"rotacion_unity": [0, 0, 0, 1], "traslacion_restada": [1.0, 0.0, 0.0], "escala_aplicada": 2.0}
    vistas, fov = lichtfeld_to_unity(str(path), man)
    assert vistas[0]["pos"] == pytest.approx([0.0, -4.0, 6.0])
    assert vistas[0]["mirar"] == pytest.approx([0.0, -4.0, 7.0])
    assert fov == pytest.approx(90.0)
