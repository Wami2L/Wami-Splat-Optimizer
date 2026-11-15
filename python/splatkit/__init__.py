"""
SplatKit: preparar escenas 3D Gaussian Splatting para ejecutarlas rápido en VR y en cualquier motor.
SplatKit: prepare 3D Gaussian Splatting scenes to run fast in VR and in any engine.

Los PLY de varios GB se procesan por bloques sobre un memmap, nunca enteros en memoria.
Multi-GB PLY files are processed in blocks over a memmap, never fully in memory.
"""

from .ply import SH_C0, SplatPly, sigmoid, write_columns, write_ply  # noqa: F401

__version__ = "0.1.0"
