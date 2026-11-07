"""Builds rnnoise.dll (Xiph RNNoise v0.1.1, BSD-3-Clause) with MSVC for Pulse's Mic studio.
Run from a Visual Studio developer prompt (CI uses ilammy/msvc-dev-cmd). Usage: python tools/build-rnnoise.py <out.dll>"""
import os, subprocess, sys, tempfile

out = os.path.abspath(sys.argv[1])
work = os.path.join(tempfile.gettempdir(), "rnnoise-src")
if not os.path.isdir(work):
    subprocess.check_call(["git", "clone", "-q", "--depth", "1", "--branch", "v0.1.1", "https://github.com/xiph/rnnoise", work])

# MSVC has no variable-length arrays: the one in celt_lpc.c becomes a stack allocation.
lpc = os.path.join(work, "src", "celt_lpc.c")
src = open(lpc, encoding="utf-8").read()
if "opus_val16 xx[n];" in src:
    src = src.replace("opus_val16 xx[n];", "opus_val16 *xx = (opus_val16*)_alloca(n * sizeof(opus_val16));")
    src = src.replace('#include "celt_lpc.h"', '#include <malloc.h>\n#include "celt_lpc.h"', 1)
    open(lpc, "w", encoding="utf-8").write(src)

files = ["denoise.c", "rnn.c", "rnn_data.c", "rnn_reader.c", "pitch.c", "kiss_fft.c", "celt_lpc.c"]
os.makedirs(os.path.dirname(out), exist_ok=True)
subprocess.check_call(["cl", "/nologo", "/O2", "/LD", "/DWIN32", "/DRNNOISE_BUILD", "/DDLL_EXPORT", "/D_USE_MATH_DEFINES",
                       "/D_CRT_SECURE_NO_WARNINGS", "/I", os.path.join(work, "include")] + [os.path.join(work, "src", f) for f in files] +
                      ["/Fe:" + out, "/Fo:" + os.path.join(work, "")], cwd=work)
print("built", out, os.path.getsize(out), "bytes")
