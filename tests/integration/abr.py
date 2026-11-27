"""Test d'intégration d'Adaptive Bitrate sur un vrai Jellyfin (Docker), lancé par .github/workflows/integration.yml.

    python abr.py media <dossier>     vidéo de test (1080p, 8 Mb/s, deux pistes audio, sous-titres) avec le ffmpeg de Jellyfin
    python abr.py run                 mise en place, vérifications par l'API, puis lecture dans un vrai navigateur

Rien de réel : un motif de test généré, un compte créé pour l'occasion (mot de passe aléatoire, jamais écrit), aucun
secret. Rapport : $ABR_OUT/report.md (résumé du job) et report.json ; code de sortie non nul au moindre problème.
"""
from __future__ import annotations

import json
import os
import re
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

BASE = os.environ.get("ABR_URL", "http://127.0.0.1:8096")
OUT = Path(os.environ.get("ABR_OUT", "abr-out"))
PROBE = Path(os.environ.get("ABR_PROBE", "probe"))      # monté dans le conteneur en /probe (ffprobe de Jellyfin)
JF = os.environ.get("ABR_CONTAINER", "jf")
PLUGIN_ID = "3c8e5a71-2f4b-4d96-a1e0-7b9d64c2f815"
DEVICE = "abr-test-" + secrets.token_hex(4)
CLIENT = f'MediaBrowser Client="Adaptive Bitrate test", Device="CI", DeviceId="{DEVICE}", Version="1.0"'
TOKEN = ""
USER_ID = ""
problems: list[str] = []
report: dict = {}
SOURCE_BITRATE = 8_000_000
DURATION = 150


def log(*a):
    print(*a, flush=True)


class HttpError(Exception):
    def __init__(self, status, method, path, detail=""):
        super().__init__(f"HTTP {status} {method} {path} {detail}")
        self.status = status


def http(method: str, path: str, body=None, query: dict | None = None, raw=False, timeout=120):
    url = path if path.startswith("http") else BASE + path
    if query:
        url += ("&" if "?" in url else "?") + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None})
    data = None if body is None else json.dumps(body).encode()
    auth = CLIENT + (f', Token="{TOKEN}"' if TOKEN else "")
    req = urllib.request.Request(url, data=data, method=method, headers={"Authorization": auth, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            content = r.read()
            if raw:
                return content
            return json.loads(content) if content and r.headers.get_content_type() == "application/json" else content.decode(errors="replace")
    except urllib.error.HTTPError as e:
        detail = e.read()[:200].decode(errors="replace")
        e.close()
        raise HttpError(e.code, method, path.split("?")[0], detail) from None


def wait(what, fn, timeout=300, every=3):
    end = time.time() + timeout
    while True:
        try:
            r = fn()
            if r:
                return r
        except (HttpError, urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        if time.time() > end:
            raise RuntimeError("délai dépassé : " + what)
        time.sleep(every)


def check(ok: bool, text: str):
    if not ok:
        problems.append(text)
    log(("✔ " if ok else "✖ ") + text)
    return ok


# ------------------------------------------------------------------ vidéo de test

def media(folder: str):
    """1080p 24 images/s, 8 Mb/s, 150 s, image clé toutes les 2 s ; audio anglais et français (stéréo AAC) ; sous-titres SRT."""
    d = Path(folder) / "movies" / "Adaptive Test Pattern (2026)"
    d.mkdir(parents=True, exist_ok=True)
    img = os.environ.get("ABR_IMAGE", "jellyfin/jellyfin:12.4")
    subprocess.run(["docker", "run", "--rm", "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-v", f"{d.resolve()}:/m", img,
                    "-loglevel", "error", "-f", "lavfi", "-i", f"testsrc2=size=1920x1080:rate=24:duration={DURATION}",
                    "-f", "lavfi", "-i", f"sine=frequency=440:sample_rate=48000:duration={DURATION}",
                    "-f", "lavfi", "-i", f"sine=frequency=660:sample_rate=48000:duration={DURATION}",
                    "-map", "0", "-map", "1", "-map", "2", "-c:v", "libx264", "-preset", "veryfast", "-b:v", "8M", "-maxrate", "8M",
                    "-bufsize", "16M", "-g", "48", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                    "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fre",
                    "/m/Adaptive Test Pattern (2026).mkv"], check=True)
    lines = []
    for i in range(DURATION // 5):
        a, b = i * 5, i * 5 + 4
        lines.append(f"{i + 1}\n00:{a // 60:02d}:{a % 60:02d},000 --> 00:{b // 60:02d}:{b % 60:02d},000\nSubtitle line {i + 1}\n")
    (d / "Adaptive Test Pattern (2026).en.srt").write_text("\n".join(lines), encoding="utf-8")
    log("vidéo de test :", sorted(p.name for p in d.iterdir()))


# ------------------------------------------------------------------ mise en place

def setup():
    global TOKEN, USER_ID
    info = wait("démarrage de Jellyfin", lambda: (lambda i: i if isinstance(i, dict) and i.get("Version") else None)(http("GET", "/System/Info/Public")))
    report["jellyfin"] = info.get("Version")
    log("Jellyfin", info.get("Version"))
    password = secrets.token_urlsafe(24)   # jamais écrit
    if not info.get("StartupWizardCompleted"):
        http("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        http("GET", "/Startup/User")
        http("POST", "/Startup/User", {"Name": "admin", "Password": password})
        http("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True})
        http("POST", "/Startup/Complete")
    auth = http("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": password})
    TOKEN, USER_ID = auth["AccessToken"], auth["User"]["Id"]
    print(f"::add-mask::{TOKEN}", flush=True)
    http("POST", "/Library/VirtualFolders", {"LibraryOptions": {"PathInfos": [{"Path": "/media/movies"}]}},
         {"name": "Movies", "collectionType": "movies", "refreshLibrary": "true"})
    def first_movie():
        items = (http("GET", "/Items", query={"IncludeItemTypes": "Movie", "Recursive": "true", "userId": USER_ID}) or {}).get("Items") or []
        return items[0] if items else None   # bibliothèque encore vide pendant l'analyse : nouvel essai
    item = wait("analyse de la bibliothèque", first_movie, timeout=300, every=5)
    plugins = http("GET", "/Plugins")
    check(any(p.get("Id", "").replace("-", "") == PLUGIN_ID.replace("-", "") and p.get("Status") == "Active" for p in plugins),
          "plugin chargé et actif : " + ", ".join(f"{p['Name']} {p['Version']} ({p.get('Status')})" for p in plugins))
    return item


def configure(**values):
    cfg = http("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    cfg.update(values)
    http("POST", f"/Plugins/{PLUGIN_ID}/Configuration", cfg)


PROFILE = {
    "MaxStreamingBitrate": 120_000_000,
    "DirectPlayProfiles": [{"Container": "mp4,mkv", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}],
    "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac", "Protocol": "hls",
                             "Context": "Streaming", "MaxAudioChannels": "2", "MinSegments": 1, "BreakOnNonKeyFrames": True}],
    "SubtitleProfiles": [{"Format": "vtt", "Method": "Hls"}, {"Format": "srt", "Method": "External"}],
    "CodecProfiles": [], "ContainerProfiles": [],
}


def playback(item, bitrate, audio=None, subtitle=None, profile=None, direct=True):
    body = {"UserId": USER_ID, "DeviceProfile": profile or PROFILE, "MaxStreamingBitrate": bitrate, "AutoOpenLiveStream": True,
            "EnableDirectPlay": direct, "EnableDirectStream": True, "EnableTranscoding": True, "AllowVideoStreamCopy": True, "AllowAudioStreamCopy": True}
    if audio is not None:
        body["AudioStreamIndex"] = audio
    if subtitle is not None:
        body["SubtitleStreamIndex"] = subtitle
    return http("POST", f"/Items/{item['Id']}/PlaybackInfo", body, {"userId": USER_ID})


def master(info):
    src = info["MediaSources"][0]
    url = src.get("TranscodingUrl")
    if not url:
        return src, None, None
    return src, BASE + url, http("GET", BASE + url)


def variants(playlist: str):
    lines = [l.strip() for l in playlist.splitlines()]
    out = []
    for i, l in enumerate(lines):
        if l.startswith("#EXT-X-STREAM-INF:"):
            uri = next((x for x in lines[i + 1:] if x and not x.startswith("#")), "")
            attr = dict((m.group(1), m.group(2).strip('"')) for m in re.finditer(r'([A-Z-]+)=("[^"]*"|[^,]*)', l.split(":", 1)[1]))
            out.append({"bandwidth": int(attr.get("BANDWIDTH", 0)), "resolution": attr.get("RESOLUTION", ""), "uri": uri,
                        "query": {k.lower(): v for k, v in urllib.parse.parse_qsl(urllib.parse.urlparse(uri).query)}})
    return out


def height(v):
    return int(v["resolution"].split("x")[1]) if "x" in v["resolution"] else 0


# ------------------------------------------------------------------ vérifications par l'API

def api_checks(item):
    # pistes : connues une fois le fichier analysé (peu après son apparition dans la bibliothèque)
    streams = wait("analyse des pistes de la vidéo", lambda: (lambda m: m if sum(1 for s in m if s["Type"] == "Audio") >= 2 else None)(
        http("GET", f"/Items/{item['Id']}", query={"userId": USER_ID}).get("MediaStreams") or []), timeout=180)
    audio = [s["Index"] for s in streams if s["Type"] == "Audio"]
    subs = [s["Index"] for s in streams if s["Type"] == "Subtitle"]
    check(len(audio) == 2 and len(subs) >= 1, f"vidéo de test : pistes audio {audio}, sous-titres {subs}")
    a2, s1 = audio[-1], subs[0]

    # 1. transcodage (6 Mb/s demandés sur un fichier à 8 Mb/s) : échelle complète sous la qualité de départ
    configure(Enabled=True, Scope="All", MaxRungs=4, Step=0.6, MinBitrateKbps=700, RungsAbove=0, PreferAdaptive=False)
    src, murl, text = master(playback(item, 6_000_000, audio=a2, subtitle=s1))
    check(text is not None, "transcodage : une playlist HLS maîtresse")
    v = variants(text or "")
    report["ladder_transcode"] = [(x["bandwidth"], x["resolution"], x["query"].get("videobitrate")) for x in v]
    check(len(v) >= 4, f"transcodage : {len(v)} qualités ({[x['resolution'] for x in v]})")
    check(all(a["bandwidth"] > b["bandwidth"] for a, b in zip(v, v[1:])), "transcodage : débits décroissants")
    check(all(height(a) >= height(b) for a, b in zip(v, v[1:])) and height(v[-1]) < height(v[0]), "transcodage : images de plus en plus petites")
    top = v[0]["query"] if v else {}
    for x in v[1:]:
        q = x["query"]
        same = all(q.get(k) == top.get(k) for k in ("playsessionid", "deviceid", "audiostreamindex", "subtitlestreamindex", "mediasourceid", "segmentcontainer"))
        if not check(same and q.get("allowvideostreamcopy") == "false", f"marche {q.get('videobitrate')} : même session, piste audio, sous-titres, conteneur ; vidéo jamais copiée"):
            break
    report["top_query"] = {k: v for k, v in top.items() if k not in ("api_key", "apikey", "deviceid", "playsessionid", "tag")}
    check(len(set(x["query"].get("audiostreamindex") for x in v)) == 1, "même piste audio dans toutes les qualités")

    # 2. segments : mêmes durées dans toutes les qualités, et passage d'une qualité à l'autre sans trou ni chevauchement
    base = murl.split("/master.m3u8")[0] + "/"
    lists = {}
    for x in (v[0], v[-1]):
        lists[x["uri"]] = http("GET", base + x["uri"])
    durs = [re.findall(r"#EXTINF:([\d.]+)", t) for t in lists.values()]
    check(len(durs[0]) > 10 and durs[0] == durs[1], f"segments : {len(durs[0])} et {len(durs[1])}, mêmes durées dans les deux qualités")
    segs = [[l for l in t.splitlines() if l and not l.startswith("#")] for t in lists.values()]
    switch(base, segs[0], segs[1], 5)
    switch(base, segs[1], segs[0], 9)

    # 3. sous-titres incrustés : chaque qualité les incruste aussi
    burn = dict(PROFILE, SubtitleProfiles=[{"Format": "srt", "Method": "Encode"}])
    _, _, text = master(playback(item, 6_000_000, audio=a2, subtitle=s1, profile=burn))
    vb = variants(text or "")
    check(len(vb) >= 3 and all(x["query"].get("subtitlemethod", "").lower() == "encode" and x["query"].get("subtitlestreamindex") == str(s1) for x in vb),
          f"sous-titres incrustés : {len(vb)} qualités, toutes avec SubtitleMethod=Encode et la piste {s1}")

    # 4. plugin coupé, ou lecteur local avec « hors du réseau local » : la playlist de Jellyfin telle quelle
    configure(Enabled=False)
    _, _, text = master(playback(item, 6_000_000))
    check(len(variants(text or "")) == 1, "plugin coupé : une seule qualité (playlist de Jellyfin)")
    configure(Enabled=True, Scope="Remote")
    _, _, text = master(playback(item, 6_000_000))
    check(len(variants(text or "")) == 1, "lecteur local, réglage « hors du réseau local » : une seule qualité")

    # 5. flux adaptatif plutôt que lecture directe : vidéo copiée en tête, transcodages dessous
    configure(Enabled=True, Scope="All", PreferAdaptive=False)
    src, _, _ = master(playback(item, 100_000_000))
    check(src.get("SupportsDirectPlay") is True, "option coupée : lecture directe possible")
    configure(PreferAdaptive=True)
    src, murl, text = master(playback(item, 100_000_000))
    v = variants(text or "")
    report["ladder_remux"] = [(x["bandwidth"], x["resolution"], x["query"].get("videobitrate")) for x in v]
    check(src.get("SupportsDirectPlay") is False and text is not None, "option active : pas de lecture directe, flux HLS")
    check(len(v) >= 4 and v[0]["query"].get("allowvideostreamcopy") != "false", f"option active : vidéo copiée en tête, {len(v) - 1} qualités transcodées dessous")
    check(all(a["bandwidth"] > b["bandwidth"] for a, b in zip(v, v[1:])) and len(v) >= 2 and height(v[-1]) < height(v[0]),
          f"option active : qualités plus basses que le fichier, débits et images décroissants ({[(x['bandwidth'] // 1000, x['resolution']) for x in v]})")
    if len(v) >= 2:
        base = murl.split("/master.m3u8")[0] + "/"
        a, b = http("GET", base + v[0]["uri"]), http("GET", base + v[-1]["uri"])
        sa, sb = [[l for l in t.splitlines() if l and not l.startswith("#")] for t in (a, b)]
        switch(base, sa, sb, 6, tolerance=0.6)   # copie : segments coupés aux images clés de la source

    status = http("GET", "/AdaptiveBitrate/Status", query={"lang": "fr"})
    check(bool(status.get("sessions")) and status.get("compatible") is True, f"page de configuration : {len(status.get('sessions') or [])} lectures listées, compatible")


def probe(path: Path) -> dict:
    r = subprocess.run(["docker", "exec", JF, "/usr/lib/jellyfin-ffmpeg/ffprobe", "-v", "error", "-show_streams", "-show_format",
                        "-show_entries", "packet=pts_time,flags", "-read_intervals", "%+#1", "-of", "json", "/probe/" + path.name],
                       capture_output=True, text=True)
    return json.loads(r.stdout or "{}")


def switch(base, from_segs, to_segs, k, tolerance=0.15):
    """Segment k d'une qualité puis k+1 d'une autre (un changement de qualité) : la suite commence où le premier finit."""
    PROBE.mkdir(exist_ok=True)
    a, b = PROBE / f"a{k}.ts", PROBE / f"b{k + 1}.ts"
    a.write_bytes(http("GET", base + from_segs[k], raw=True, timeout=300))
    b.write_bytes(http("GET", base + to_segs[k + 1], raw=True, timeout=300))
    pa, pb = probe(a), probe(b)
    fa, fb = pa.get("format", {}), pb.get("format", {})
    end_a = float(fa.get("start_time", 0)) + float(fa.get("duration", 0))
    start_b = float(fb.get("start_time", 0))
    va = [s for s in pb.get("streams", []) if s.get("codec_type") == "video"]
    aa = [(s.get("codec_name"), s.get("channels"), s.get("sample_rate")) for s in pa.get("streams", []) if s.get("codec_type") == "audio"]
    ab = [(s.get("codec_name"), s.get("channels"), s.get("sample_rate")) for s in pb.get("streams", []) if s.get("codec_type") == "audio"]
    key = any("K" in (p.get("flags") or "") for p in (pb.get("packets") or [])[:1])
    report.setdefault("switches", []).append({"k": k, "end_a": round(end_a, 3), "start_b": round(start_b, 3), "audio_a": aa, "audio_b": ab})
    check(abs(start_b - end_a) <= tolerance, f"changement de qualité au segment {k + 1} : fin {end_a:.3f} s, suite à {start_b:.3f} s")
    check(bool(va) and key, f"segment {k + 1} de la nouvelle qualité : commence par une image clé")
    check(aa == ab and bool(aa), f"segment {k + 1} : même audio des deux côtés {aa}")


# ------------------------------------------------------------------ lecture dans un vrai navigateur

VIDEO_JS = """() => { const v = document.querySelector('video'); if (!v) return null;
  return { t: v.currentTime, paused: v.paused, ready: v.readyState, ended: v.ended,
           buf: v.buffered.length ? v.buffered.end(v.buffered.length - 1) - v.currentTime : 0,
           audio: v.webkitAudioDecodedByteCount || 0, frames: v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality().totalVideoFrames : 0 }; }"""


def browser_run(item, label: str):
    """Lecture de 130 s : réseau libre 30 s, bridé à 1,6 Mb/s 60 s, libre 40 s. Coupures, qualités, redémarrages."""
    from playwright.sync_api import sync_playwright
    info = http("GET", "/System/Info/Public")
    creds = {"Servers": [{"Id": info["Id"], "ManualAddress": BASE, "LocalAddress": BASE, "UserId": USER_ID, "AccessToken": TOKEN,
                          "LastConnectionMode": 2, "DateLastAccessed": int(time.time() * 1000)}]}
    res = {"label": label, "variants": [], "playbackinfo": 0, "stall_s": 0.0, "stalls": 0, "errors": []}
    measuring = [False]
    with sync_playwright() as p:
        b = p.chromium.launch(channel="chrome", args=["--autoplay-policy=no-user-gesture-required"])
        ctx = b.new_context(viewport={"width": 1280, "height": 720})
        ctx.add_init_script("localStorage.setItem('jellyfin_credentials', %s);" % json.dumps(json.dumps(creds)))
        page = ctx.new_page()
        page.on("pageerror", lambda e: res["errors"].append(str(e)[:200]))

        def on_request(r):
            u = r.url
            if "/PlaybackInfo" in u and r.method == "POST" and measuring[0]:
                res["playbackinfo"] += 1   # une nouvelle demande de lecture en cours de route = la lecture a redémarré
            if "/main.m3u8" in u:
                q = {k.lower(): v for k, v in urllib.parse.parse_qsl(urllib.parse.urlparse(u).query)}
                res["variants"].append((round(time.time() - start, 1), q.get("videobitrate", "copie")))
        start = time.time()
        page.on("request", on_request)
        page.goto(f"{BASE}/web/#/details?id={item['Id']}", wait_until="domcontentloaded")
        page.wait_for_selector(".btnPlay", timeout=90000)
        page.click(".btnPlay")
        page.wait_for_function("() => { const v = document.querySelector('video'); return v && v.currentTime > 1; }", timeout=120000)
        cdp = ctx.new_cdp_session(page)
        cdp.send("Network.enable")
        start = time.time()
        measuring[0] = True
        samples, phase_of = [], lambda s: "libre" if s < 30 else ("bridé" if s < 90 else "libre")
        last_t, waiting_since = None, None
        while time.time() - start < 130:
            s = time.time() - start
            kbps = 1600 if 30 <= s < 90 else -1
            cdp.send("Network.emulateNetworkConditions", {"offline": False, "latency": 20, "downloadThroughput": kbps * 1000 / 8 if kbps > 0 else -1,
                                                          "uploadThroughput": -1})
            v = page.evaluate(VIDEO_JS)
            if v:
                stuck = last_t is not None and abs(v["t"] - last_t) < 0.05 and not v["paused"] and not v["ended"]
                if stuck:
                    waiting_since = waiting_since or s
                elif waiting_since is not None:
                    res["stall_s"] += s - waiting_since
                    res["stalls"] += 1
                    waiting_since = None
                if last_t is not None and v["t"] + 0.5 < last_t:
                    res["errors"].append(f"recul de la lecture à {s:.0f} s ({last_t:.1f} → {v['t']:.1f})")
                last_t = v["t"]
                samples.append({"s": round(s, 1), "phase": phase_of(s), **{k: (round(x, 2) if isinstance(x, float) else x) for k, x in v.items()}})
            time.sleep(1)
        if waiting_since is not None:
            res["stall_s"] += time.time() - start - waiting_since
            res["stalls"] += 1
        res["stall_s"] = round(res["stall_s"], 1)
        res["position"] = samples[-1]["t"] if samples else 0
        res["audio_growing"] = len(samples) > 10 and samples[-1]["audio"] > samples[len(samples) // 2]["audio"] > samples[0]["audio"]
        page.screenshot(path=str(OUT / f"lecture-{label}.png"))
        b.close()
    res["samples"] = samples
    return res


def browser_checks(item):
    # même flux HLS dans les deux essais (vidéo copiée en tête) : avec l'échelle, puis sans (aucune qualité dessous)
    configure(Enabled=True, Scope="All", PreferAdaptive=True, MaxRungs=4)
    on = browser_run(item, "echelle")
    configure(Enabled=True, Scope="All", PreferAdaptive=True, MaxRungs=0)
    off = browser_run(item, "sans-echelle")
    configure(MaxRungs=4)
    report["browser"] = {"with": {k: v for k, v in on.items() if k != "samples"}, "without": {k: v for k, v in off.items() if k != "samples"}}
    (OUT / "browser.json").write_text(json.dumps({"with": on, "without": off}, indent=1), encoding="utf-8")
    # première qualité demandée = celle de départ ; une demande ensuite d'une autre playlist = un changement de qualité
    start = on["variants"][0][1] if on["variants"] else None
    changed = [x for x in on["variants"][1:] if x[1] != start]
    check(len(changed) >= 1, f"navigateur : le lecteur a changé de qualité pendant la baisse du réseau ({on['variants']})")
    check(on["playbackinfo"] == 0, f"navigateur : aucun redémarrage de la lecture ({on['playbackinfo']} nouvelle demande de lecture)")
    check(not [e for e in on["errors"] if "recul" in e], f"navigateur : la lecture n'a jamais reculé ({on['errors'][:3]})")
    check(on["audio_growing"], "navigateur : le son continue d'être décodé pendant toute la lecture")
    check(on["stall_s"] <= max(3.0, off["stall_s"] * 0.5),
          f"navigateur : coupures {on['stall_s']} s avec l'échelle contre {off['stall_s']} s sans (même réseau)")


def write_report():
    OUT.mkdir(exist_ok=True)
    (OUT / "report.json").write_text(json.dumps({"problems": problems, **report}, indent=1, ensure_ascii=False), encoding="utf-8")
    L = [f"## Adaptive Bitrate sur Jellyfin {report.get('jellyfin')}", ""]
    if report.get("ladder_transcode"):
        L += ["**Échelle (transcodage, 6 Mb/s demandés)** : " + " · ".join(f"{r} à {b // 1000} kb/s" for b, r, _ in report["ladder_transcode"]), ""]
    if report.get("ladder_remux"):
        L += ["**Échelle (vidéo copiée en tête)** : " + " · ".join(f"{r} à {b // 1000} kb/s" for b, r, _ in report["ladder_remux"]), ""]
    for s in report.get("switches", []):
        L.append(f"- Changement de qualité au segment {s['k'] + 1} : fin {s['end_a']} s, suite à {s['start_b']} s, audio {s['audio_a']} → {s['audio_b']}")
    br = report.get("browser")
    if br:
        L += ["", "| Navigateur (réseau libre 30 s, 1,6 Mb/s 60 s, libre 40 s) | Avec l'échelle | Sans |", "|---|---|---|"]
        for k, name in (("stall_s", "Coupures (s)"), ("stalls", "Nombre de coupures"), ("playbackinfo", "Demandes de lecture"), ("position", "Position atteinte (s)")):
            L.append(f"| {name} | {br['with'].get(k)} | {br['without'].get(k)} |")
        L.append(f"| Qualités demandées (s, débit) | {br['with'].get('variants')} | {br['without'].get('variants')} |")
    L += ["", "### " + ("Aucun problème détecté" if not problems else f"{len(problems)} problème(s)")] + [f"- {p}" for p in problems]
    (OUT / "report.md").write_text("\n".join(L) + "\n", encoding="utf-8")
    print("\n".join(L))


def run():
    OUT.mkdir(exist_ok=True)
    try:
        item = setup()
        api_checks(item)
        if os.environ.get("ABR_BROWSER"):
            browser_checks(item)
    except Exception as e:   # le rapport dit où le test s'est arrêté
        problems.append(f"arrêt : {type(e).__name__}: {str(e)[:300]}")
        raise
    finally:
        write_report()
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    {"media": lambda: media(sys.argv[2]), "run": run}[sys.argv[1]]()
