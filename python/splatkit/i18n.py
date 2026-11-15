"""
Mensajes en español e inglés / Spanish and English messages.

Cada texto se escribe en los dos idiomas en el mismo sitio: ``t("Leyendo {n} splats", "Reading {n} splats", n=5)``.
El idioma sale de (en orden): ``set_lang()``, la variable ``SPLATKIT_LANG`` (``es`` / ``en``) o el idioma del sistema.

Every message is written in both languages side by side. The language comes from ``set_lang()``, the
``SPLATKIT_LANG`` environment variable (``es`` / ``en``) or the system language.
"""

import locale
import os

LANGS = ("es", "en")
_lang = None


def _system_lang():
    try:
        if os.name == "nt":
            import ctypes
            lcid = ctypes.windll.kernel32.GetUserDefaultUILanguage()
            return "es" if (lcid & 0x3FF) == 0x0A else "en"     # LANG_SPANISH = 0x0A
        code = (locale.getlocale()[0] or os.environ.get("LANG", "")).lower()
        return "es" if code.startswith("es") else "en"
    except Exception:
        return "en"


def lang():
    global _lang
    if _lang is None:
        env = os.environ.get("SPLATKIT_LANG", "").lower()[:2]
        _lang = env if env in LANGS else _system_lang()
    return _lang


def set_lang(value):
    """Fija el idioma ('es' o 'en'); None vuelve al automático. / Set the language; None = automatic."""
    global _lang
    if value is None:
        _lang = None
        return
    value = value.lower()[:2]
    if value not in LANGS:
        raise ValueError(f"idioma no soportado / unsupported language: {value}")
    _lang = value


def t(es, en, **kw):
    """Texto en el idioma actual, con ``str.format(**kw)``. / Text in the current language."""
    s = es if lang() == "es" else en
    return s.format(**kw) if kw else s


def pick(d):
    """Elige de un dict ``{"es": ..., "en": ...}`` (o devuelve el valor tal cual). / Pick from a bilingual dict."""
    if isinstance(d, dict):
        return d.get(lang()) or d.get("en") or d.get("es") or ""
    return d
