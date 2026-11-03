"""
Downloads portrait cover art for the games Pulse shows (its game checklists and the "Find something new" list)
from Steam's public store, at build time only. The images are embedded in PulseOptimizer.exe so the app itself
never goes online. Game art belongs to its publishers and is used only to identify each game.

Usage: python tools/get-game-art.py <out-dir>
Games that aren't on Steam (VALORANT, Fortnite, Roblox, ...) are skipped; the app uses their installed icon instead.
"""
import json, os, re, sys, time, urllib.parse, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "PulseOptimizer", "Core", "Games")
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "src", "PulseOptimizer", "Assets", "Games")
UA = {"User-Agent": "PulseOptimizer-build"}
# Names Steam lists differently.
ALIASES = {
    "Call of Duty: Warzone": "Call of Duty",
    "Overwatch 2": "Overwatch",
    "Rainbow Six Siege": "Tom Clancy's Rainbow Six Siege",
}
SKIP = {"Any other game"}
# Games the store search no longer returns (delisted for new buyers, or a symbol in the name), by Steam app id.
KNOWN = {"Rocket League": 252950, "Fall Guys": 1097150, "Battlefield 6": 2807960, "F1 25": 3059520, "PEAK": 3527290}
# Art you add by hand (games not on Steam) lives in assets/game-art/<key>.jpg|png and always wins.
MANUAL = os.path.join(ROOT, "assets", "game-art")

def norm(s): return re.sub(r"[^a-z0-9]", "", s.lower())

def key_for(name): return norm(name)

def get(url, binary=False):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=20) as r:
        data = r.read()
    return data if binary else json.loads(data.decode("utf-8"))

def find_app(name):
    term = ALIASES.get(name, name)
    res = get("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + urllib.parse.quote(term))
    want = norm(term)
    items = [i for i in res.get("items", []) if i.get("type") == "app"]
    for item in items:
        got = norm(item.get("name", ""))
        if got == want or got.startswith(want) and len(got) - len(want) <= 3:
            return item["id"]
    # Looser second pass: the name appears inside Steam's title (e.g. "Battlefield™ 6", "EA SPORTS F1® 25").
    for item in items:
        got = norm(item.get("name", ""))
        if len(want) >= 4 and want in got and len(got) - len(want) <= 12:
            return item["id"]
    return None

def art(app_id):
    # Newer games keep their art under a hashed folder; appdetails tells us where.
    try:
        info = get(f"https://store.steampowered.com/api/appdetails?appids={app_id}&l=english")
        data = (info.get(str(app_id)) or {}).get("data") or {}
        header = data.get("header_image", "")
        m = re.search(r"/apps/\d+/([0-9a-f]{40})/", header)
        if m:
            for name in ("library_600x900_2x.jpg", "library_600x900.jpg"):
                try:
                    d = get(f"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{app_id}/{m.group(1)}/{name}", binary=True)
                    if len(d) > 5000: return d
                except Exception:
                    pass
    except Exception:
        pass
    for url in (f"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{app_id}/library_600x900.jpg",
                f"https://cdn.akamai.steamstatic.com/steam/apps/{app_id}/library_600x900.jpg"):
        try:
            data = get(url, binary=True)
            if len(data) > 5000: return data
        except Exception:
            pass
    return None

def names():
    out = []
    cat = open(os.path.join(SRC, "GameCatalog.cs"), encoding="utf-8").read()
    out += re.findall(r'new\("[a-z0-9]+", "([^"]+)", "', cat)
    sug = open(os.path.join(SRC, "Suggestions.cs"), encoding="utf-8").read()
    out += re.findall(r'^\s*new\("([^"]+)", "', sug, re.M)
    return list(dict.fromkeys(out))

def main():
    os.makedirs(OUT, exist_ok=True)
    found = 0
    import shutil
    if os.path.isdir(MANUAL):
        for f in os.listdir(MANUAL):
            stem, ext = os.path.splitext(f)
            if ext.lower() in (".jpg", ".jpeg", ".png"):
                shutil.copy(os.path.join(MANUAL, f), os.path.join(OUT, norm(stem) + ".jpg" if ext.lower() != ".png" else norm(stem) + ".png"))
                print(f"manual {f}")
    for name in names():
        if name in SKIP: continue
        path = os.path.join(OUT, key_for(name) + ".jpg")
        if os.path.exists(path) or os.path.exists(path[:-4] + ".png"): continue
        try:
            app = find_app(name) or KNOWN.get(name)
            data = art(app) if app else None
            if data:
                open(path, "wb").write(data); found += 1
                print(f"ok    {name} (app {app})")
            else:
                print(f"skip  {name} (not on Steam)")
        except Exception as e:
            print(f"skip  {name} ({e})")
        time.sleep(0.4)
    print(f"{found} covers saved to {OUT}")

if __name__ == "__main__":
    main()
