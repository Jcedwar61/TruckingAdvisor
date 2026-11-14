"""
Builds src/TruckingAdvisor.Core/Mapping/Data/geo.json.gz from Natural Earth (public domain).

    python3 tools/build_gazetteer.py <folder with the Natural Earth GeoJSON files>

Inputs (from https://github.com/nvkelso/natural-earth-vector/tree/master/geojson):
  ne_10m_populated_places_simple.geojson, ne_10m_roads.geojson,
  ne_10m_admin_1_states_provinces_lakes.geojson, ne_50m_admin_0_countries.geojson

Output: towns, areas (US states, Canadian provinces, Mexican states, European countries) and
major highways for the two regions the games cover, simplified to keep the file small.
"""
import gzip, json, math, os, sys

src = sys.argv[1]
out = os.path.join(os.path.dirname(__file__), "..", "src", "TruckingAdvisor.Core", "Mapping", "Data", "geo.json.gz")

NA = {"USA", "CAN", "MEX"}
EUROPE_EXTRA = {"TUR", "RUS", "UKR", "BLR", "KAZ", "GEO", "ARM", "AZE", "CYP", "MAR", "TUN", "DZA"}

def load(name):
    with open(os.path.join(src, name), encoding="utf-8") as f:
        return json.load(f)["features"]

def perp(p, a, b):
    (x, y), (x1, y1), (x2, y2) = p, a, b
    dx, dy = x2 - x1, y2 - y1
    if dx == 0 and dy == 0:
        return math.hypot(x - x1, y - y1)
    t = max(0, min(1, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (x1 + t * dx), y - (y1 + t * dy))

def simplify(pts, tol):
    if len(pts) < 3:
        return pts
    keep = [False] * len(pts); keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        i, j = stack.pop()
        best, idx = 0, -1
        for k in range(i + 1, j):
            d = perp(pts[k], pts[i], pts[j])
            if d > best: best, idx = d, k
        if best > tol:
            keep[idx] = True
            stack += [(i, idx), (idx, j)]
    return [p for p, k in zip(pts, keep) if k]

def rnd(pts):
    return [[round(x, 3), round(y, 3)] for x, y in pts]

def rings(geom):
    if geom["type"] == "Polygon":
        return [geom["coordinates"][0]]
    if geom["type"] == "MultiPolygon":
        return [poly[0] for poly in geom["coordinates"]]
    return []

def lines(geom):
    if geom["type"] == "LineString":
        return [geom["coordinates"]]
    if geom["type"] == "MultiLineString":
        return geom["coordinates"]
    return []

def in_europe_box(lon, lat):
    return -25 <= lon <= 45 and 34 <= lat <= 72

data = {"source": "Made with Natural Earth (public domain). naturalearthdata.com", "regions": {}}
for region in ("ats", "ets2"):
    data["regions"][region] = {"towns": [], "areas": [], "roads": []}

# Towns
for f in load("ne_10m_populated_places_simple.geojson"):
    p = f["properties"]
    lon, lat, a3 = p["longitude"], p["latitude"], p["adm0_a3"]
    region = "ats" if a3 in NA else "ets2" if (in_europe_box(lon, lat) and (p.get("featurecla") or "") and (a3 not in NA)) else None
    if region is None:
        continue
    names = {p["name"]}
    for alt in (p.get("nameascii"), p.get("namealt"), p.get("namepar")):
        if alt:
            names.update(n.strip() for n in str(alt).split("|") if n.strip())
    data["regions"][region]["towns"].append({
        "n": p["name"], "alt": sorted(names - {p["name"]}), "a": p.get("adm1name") or "", "c": p.get("adm0name") or "",
        "lat": round(lat, 4), "lon": round(lon, 4), "pop": int(p.get("pop_max") or 0)})

# Areas: states / provinces for North America, countries for Europe
for f in load("ne_10m_admin_1_states_provinces_lakes.geojson"):
    p = f["properties"]
    if p.get("adm0_a3") not in NA:
        continue
    rs = [rnd(simplify([tuple(c) for c in r], 0.02)) for r in rings(f["geometry"])]
    rs = [r for r in rs if len(r) >= 4]
    if rs:
        data["regions"]["ats"]["areas"].append({"n": p["name"], "c": p.get("admin") or "", "abbr": (p.get("postal") or ""), "r": rs})
for f in load("ne_50m_admin_0_countries.geojson"):
    p = f["properties"]
    if p.get("CONTINENT") != "Europe" and p.get("ADM0_A3") not in EUROPE_EXTRA:
        continue
    rs = [rnd(simplify([tuple(c) for c in r], 0.02)) for r in rings(f["geometry"])]
    rs = [r for r in rs if len(r) >= 4 and any(in_europe_box(x, y) for x, y in r)]
    if rs:
        data["regions"]["ets2"]["areas"].append({"n": p["NAME"], "c": p["NAME"], "abbr": p.get("ISO_A2") or "", "r": rs})

# Roads: numbered major highways
def us_label(p):
    n, lvl = (p.get("name") or "").strip(), p.get("level")
    if not n or not n.replace("A", "").replace("B", "").isdigit():
        return None
    return {"Interstate": f"I-{n}", "Federal": f"US-{n}", "State": f"Highway {n}"}.get(lvl)

for f in load("ne_10m_roads.geojson"):
    p = f["properties"]
    if p.get("type") in ("Ferry Route", None):
        continue
    label, region = None, None
    if p.get("sov_a3") == "USA" and p.get("type") in ("Major Highway", "Beltway", "Secondary Highway"):
        label, region = us_label(p), "ats"
        if p.get("type") == "Secondary Highway" and p.get("level") == "State":
            label = None
    elif p.get("sov_a3") in ("CAN", "MEX") and p.get("type") in ("Major Highway", "Beltway"):
        n = (p.get("name") or "").strip()
        label, region = (f"Highway {n}" if n else None), "ats"
    elif p.get("continent") == "Europe" and p.get("type") in ("Major Highway", "Beltway"):
        label, region = (p.get("label") or None), "ets2"
    if not label:
        continue
    for line in lines(f["geometry"]):
        pts = rnd(simplify([tuple(c) for c in line], 0.005))
        if len(pts) >= 2:
            data["regions"][region]["roads"].append({"n": label, "p": pts})

os.makedirs(os.path.dirname(out), exist_ok=True)
raw = json.dumps(data, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
with gzip.open(out, "wb", compresslevel=9) as g:
    g.write(raw)
for r, d in data["regions"].items():
    print(r, {k: len(v) for k, v in d.items()})
print("raw", len(raw), "gz", os.path.getsize(out))
