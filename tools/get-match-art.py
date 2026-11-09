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
    meta = {"agents": {}, "maps": {}, "weapons": {}, "count": {}}
    done = {"agents": 0, "maps": 0, "ranks": 0, "minimaps": 0, "weapons": 0}

    try:
        for a in get(f"{API}/agents?isPlayableCharacter=true")["data"]:
            k = key(a.get("displayName"))
            if a.get("uuid") and a.get("displayName"):
                meta.setdefault("agentIds", {})[a["uuid"].lower()] = a["displayName"]  # new agents get a name even before Pulse knows them
            icon = a.get("displayIcon") or a.get("displayIconSmall")
            if not k or not icon:
                continue
            try:
                save(icon, os.path.join(OUT, f"agent-{k}.png"), (256, 256))  # sharp at 64 px on 200-400% screens
                # The role icon (Duelist, Controller...), the one trackers show next to role stats.
                role = a.get("role") or {}
                rk = key(role.get("displayName"))
                if rk and role.get("displayIcon") and not os.path.exists(os.path.join(OUT, f"role-{rk}.png")):
                    try:
                        save(role["displayIcon"], os.path.join(OUT, f"role-{rk}.png"), (96, 96))
                    except Exception as e:
                        print(f"  role {rk}: {e}")
                # The bust portrait for the tracker's match page (the player card on the Performance tab).
                bust = a.get("bustPortrait") or a.get("fullPortrait")
                if bust:
                    try:
                        save(bust, os.path.join(OUT, f"bust-{k}.png"), (300, 300))
                    except Exception as e:
                        print(f"  bust {k}: {e}")
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
            # The minimap and how match positions land on it (coach heatmaps). The game's x/y are swapped on the
            # minimap: across = y * xMultiplier + xScalarToAdd, down = x * yMultiplier + yScalarToAdd.
            mini = m.get("displayIcon")
            if mini and m.get("xMultiplier"):
                try:
                    save(mini, os.path.join(OUT, f"mini-{k}.png"), (512, 512))
                    calls = []
                    for c in m.get("callouts") or []:
                        loc = c.get("location") or {}
                        if c.get("regionName") and "x" in loc and "y" in loc:
                            calls.append([c["regionName"], c.get("superRegionName") or "", loc["x"], loc["y"]])
                    meta["maps"][k] = {"m": [m["xMultiplier"], m["yMultiplier"], m["xScalarToAdd"], m["yScalarToAdd"]], "c": calls}
                    done["minimaps"] += 1
                except Exception as e:
                    print(f"  minimap {k}: {e}")
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

    try:
        meta["weaponCats"] = {}
        for w in get(f"{API}/weapons")["data"]:
            if w.get("uuid") and w.get("displayName"):
                uid = w["uuid"].lower()
                meta["weapons"][uid] = w["displayName"]
                cat = (w.get("category") or "").split("::")[-1]
                if cat:
                    meta["weaponCats"][uid] = cat
                # The white killfeed silhouette, the icon trackers show next to a weapon.
                icon = w.get("killStreamIcon") or w.get("displayIcon")
                if icon:
                    try:
                        save(icon, os.path.join(OUT, f"weapon-{uid}.png"), (192, 64))
                    except Exception as e:
                        print(f"  weapon {w['displayName']}: {e}")
                done["weapons"] += 1
    except Exception as e:
        print(f"Weapons: {e}")

    # Abilities that work like guns: Riot lists their kills as weapon kills (or ultimates), but they aren't in the
    # weapon list. Their ability icon stands in for a weapon picture. Ids match VMatch's constants.
    ability_weapons = {
        ("Chamber", "Headhunter"): "856d9a7e-4b06-dc37-15dc-9d809c37cb90",
        ("Chamber", "Tour De Force"): "39099fb5-4293-def4-1e09-2e9080ce7456",
        ("Neon", "Overdrive"): "95336ae4-45d4-1032-cfaf-6bad01910607",
        ("Jett", "Blade Storm"): "ability-blade-storm",
        ("Raze", "Showstopper"): "ability-showstopper",
    }
    try:
        for a in get(f"{API}/agents?isPlayableCharacter=true")["data"]:
            for ab in a.get("abilities") or []:
                uid = ability_weapons.get((a.get("displayName"), ab.get("displayName")))
                if uid and ab.get("displayIcon"):
                    try:
                        save(ab["displayIcon"], os.path.join(OUT, f"weapon-{uid}.png"), (64, 64))
                        done["weapons"] += 1
                    except Exception as e:
                        print(f"  ability weapon {ab['displayName']}: {e}")
    except Exception as e:
        print(f"Ability weapons: {e}")

    meta["count"] = done
    with open(os.path.join(OUT, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=1)
    print(f"Match art: {done['agents']} agents, {done['maps']} maps ({done['minimaps']} minimaps), {done['ranks']} ranks, {done['weapons']} weapons")

if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # never fail the build over pictures
        print(f"Match art skipped: {e}")
    sys.exit(0)
