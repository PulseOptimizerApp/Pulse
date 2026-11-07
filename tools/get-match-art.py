"""
Downloads VALORANT art for match recaps (agent portraits, rank badges, map backgrounds) at build time only, from
valorant-api.com, the community mirror of the game's own content. The images are embedded in PulseOptimizer.exe so
the app itself never goes online for them. The art belongs to Riot Games and is used only to show your own matches
(Riot's "Legal Jibber Jabber" policy allows free fan projects to use it).

Usage: python tools/get-match-art.py <out-dir>
Never fails the build: anything it can't get is skipped, and the card falls back to plain text.
"""
import io, json, os, re, sys, urllib.request

OUT = sys.argv[1] if len(sys.argv) > 1 else "src/PulseOptimizer/Assets/Matches"
UA = {"User-Agent": "PulseOptimizer-build"}
API = "https://valorant-api.com/v1"

try:
    from PIL import Image
except ImportError:
    Image = None

def key(s): return re.sub(r"[^a-z0-9]", "", (s or "").lower())

def get(url, binary=False):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=30) as r:
        data = r.read()
    return data if binary else json.loads(data.decode("utf-8"))

def save(url, path, size=None, jpeg=False):
    """Downloads an image, shrinks it to fit `size` (width, height) and saves it as PNG or JPEG."""
    data = get(url, binary=True)
    if Image is None:
        open(path, "wb").write(data)
        return
    im = Image.open(io.BytesIO(data))
    if size:
        im.thumbnail(size, Image.LANCZOS)
    if jpeg:
        im.convert("RGB").save(path, "JPEG", quality=82, optimize=True, progressive=True)
    else:
        im.save(path, "PNG", optimize=True)

def main():
    os.makedirs(OUT, exist_ok=True)
    meta = {"agents": {}, "count": {}}
    done = {"agents": 0, "maps": 0, "ranks": 0}

    try:
        for a in get(f"{API}/agents?isPlayableCharacter=true")["data"]:
            k = key(a.get("displayName"))
            icon = a.get("displayIcon") or a.get("displayIconSmall")
            if not k or not icon:
                continue
            try:
                save(icon, os.path.join(OUT, f"agent-{k}.png"), (128, 128))
                colors = a.get("backgroundGradientColors") or []
                if colors:
                    meta["agents"][k] = colors[0][:6]
                done["agents"] += 1
            except Exception as e:
                print(f"  agent {k}: {e}")
    except Exception as e:
        print(f"Agents: {e}")

    try:
        for m in get(f"{API}/maps")["data"]:
            k = key(m.get("displayName"))
            img = m.get("splash") or m.get("listViewIconTall") or m.get("listViewIcon")
            if not k or not img:
                continue
            try:
                save(img, os.path.join(OUT, f"map-{k}.jpg"), (640, 640), jpeg=True)
                done["maps"] += 1
            except Exception as e:
                print(f"  map {k}: {e}")
    except Exception as e:
        print(f"Maps: {e}")

    try:
        sets = get(f"{API}/competitivetiers")["data"]
        latest = sets[-1]
        for t in latest.get("tiers", []):
            tier = t.get("tier")
            icon = t.get("largeIcon") or t.get("smallIcon")
            if tier is None or tier < 3 or not icon:
                continue
            try:
                save(icon, os.path.join(OUT, f"rank-{tier}.png"), (64, 64))
                done["ranks"] += 1
            except Exception as e:
                print(f"  rank {tier}: {e}")
    except Exception as e:
        print(f"Ranks: {e}")

    meta["count"] = done
    with open(os.path.join(OUT, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1)
    print(f"Match art: {done['agents']} agents, {done['maps']} maps, {done['ranks']} ranks")

if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never fail the build over pictures
        print(f"Match art skipped: {e}")
    sys.exit(0)
