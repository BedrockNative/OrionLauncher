"""Check private Fontconfig with disposable user configuration and font caches."""
import ctypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def probe(bundle):
    library = ctypes.CDLL(str(bundle / "usr/lib/orion/native/libfontconfig.so.1"))

    class FontSet(ctypes.Structure):
        _fields_ = [("count", ctypes.c_int), ("capacity", ctypes.c_int), ("fonts", ctypes.c_void_p)]

    library.FcInitLoadConfigAndFonts.restype = ctypes.c_void_p
    library.FcConfigGetFonts.argtypes = [ctypes.c_void_p, ctypes.c_int]
    library.FcConfigGetFonts.restype = ctypes.POINTER(FontSet)
    library.FcConfigDestroy.argtypes = [ctypes.c_void_p]
    config = library.FcInitLoadConfigAndFonts()
    if not config:
        raise RuntimeError("Fontconfig could not initialize")
    try:
        fonts = library.FcConfigGetFonts(config, 0)
        if not fonts or fonts.contents.count <= 0:
            raise RuntimeError("Fontconfig found no fonts")
        print(json.dumps({"fontconfigVersion": library.FcGetVersion(), "fonts": fonts.contents.count}))
    finally:
        library.FcConfigDestroy(config)


def main(bundle):
    with tempfile.TemporaryDirectory(prefix="orion-fontconfig-") as directory:
        env = {**os.environ, "HOME": directory, "XDG_CONFIG_HOME": directory + "/config",
               "XDG_DATA_HOME": directory + "/data", "XDG_CACHE_HOME": directory + "/cache",
               "FONTCONFIG_FILE": str(bundle / "etc/fonts/fonts.conf"),
               "FONTCONFIG_PATH": str(bundle / "etc/fonts")}
        result = subprocess.run([sys.executable, __file__, str(bundle), "--probe"],
                                env=env, capture_output=True, text=True, timeout=60)
        print(result.stdout, end="")
        print(result.stderr, end="", file=sys.stderr)
        if result.returncode or "Fontconfig warning:" in result.stderr or "Fontconfig error:" in result.stderr:
            raise RuntimeError("Private Fontconfig initialization failed or parsed incompatible rules")


if __name__ == "__main__":
    target = Path(sys.argv[1]).resolve(strict=True)
    if sys.argv[2:] == ["--probe"]:
        probe(target)
    else:
        main(target)
