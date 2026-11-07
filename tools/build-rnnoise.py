"""Builds rnnoise.dll (Xiph RNNoise v0.2, BSD-3-Clause) with MSVC for Pulse's Mic studio.

v0.2 has the much better 2024 model: it removes steady noise (fans, hiss) almost completely where v0.1 only took a
little off, and its voice detection is far more reliable. The release tarball carries the trained model, so nothing
else is downloaded. Run from a Visual Studio developer prompt (CI uses ilammy/msvc-dev-cmd).
Usage: python tools/build-rnnoise.py <out.dll>"""
import hashlib, os, re, subprocess, sys, tarfile, tempfile, urllib.request

VERSION = "0.2"
URL = f"https://github.com/xiph/rnnoise/releases/download/v{VERSION}/rnnoise-{VERSION}.tar.gz"

out = os.path.abspath(sys.argv[1])
tmp = os.path.join(tempfile.gettempdir(), "rnnoise-build")
os.makedirs(tmp, exist_ok=True)
archive = os.path.join(tmp, f"rnnoise-{VERSION}.tar.gz")
if not os.path.exists(archive):
    urllib.request.urlretrieve(URL, archive)
print("rnnoise", VERSION, "sha256", hashlib.sha256(open(archive, "rb").read()).hexdigest())
work = os.path.join(tmp, f"rnnoise-{VERSION}")
if not os.path.isdir(work):
    with tarfile.open(archive) as t:
        t.extractall(tmp, filter="data")
src = os.path.join(work, "src")

# MSVC has no variable-length arrays: any one sized by a lowercase (runtime) expression becomes a stack allocation of
# the same size. v0.2 has none today; this keeps the build working if one comes back.
vla = re.compile(r"^(\s*)(opus_val16|opus_val32|float|int|kiss_fft_cpx)\s+(\w+)\[([^\]]*[a-z][^\]]*)\];", re.M)
for name in os.listdir(src):
    if not name.endswith(".c"):
        continue
    path = os.path.join(src, name)
    text = open(path, encoding="utf-8").read()
    fixed = vla.sub(lambda m: f"{m.group(1)}{m.group(2)} *{m.group(3)} = ({m.group(2)}*)_alloca(({m.group(4)}) * sizeof({m.group(2)}));", text)
    if fixed != text:
        open(path, "w", encoding="utf-8").write("#include <malloc.h>\n" + fixed)
        print("patched variable-length arrays in", name)

# Plain C (no AVX2/SSE4.1 dispatch), so it runs on every x64 CPU. It costs about 1% of one core.
files = ["denoise.c", "rnn.c", "pitch.c", "kiss_fft.c", "celt_lpc.c", "nnet.c", "nnet_default.c",
         "parse_lpcnet_weights.c", "rnnoise_data.c", "rnnoise_tables.c"]
os.makedirs(os.path.dirname(out), exist_ok=True)
subprocess.check_call(["cl", "/nologo", "/O2", "/LD", "/DWIN32", "/DRNNOISE_BUILD", "/DDLL_EXPORT", "/D_USE_MATH_DEFINES",
                       "/D_CRT_SECURE_NO_WARNINGS", "/I", os.path.join(work, "include"), "/I", src]
                      + [os.path.join(src, f) for f in files] + ["/Fe:" + out, "/Fo:" + os.path.join(tmp, "")], cwd=tmp)
print("built", out, os.path.getsize(out), "bytes")
