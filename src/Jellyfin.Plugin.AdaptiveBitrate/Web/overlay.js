// Adaptive Bitrate : surcouche de diagnostic pendant une lecture dans le client web (activée dans les réglages du
// plugin). Mesure côté navigateur ce qui arrive vraiment (temps de téléchargement de chaque requête, Resource Timing) :
// qualité en cours (playlist demandée), débit reçu par segment, tampon d'avance, image affichée, images perdues ; le
// serveur donne les qualités proposées et l'historique des changements de la lecture. Aucun texte venu du serveur n'est
// inséré comme du HTML (textContent seulement). Rien n'est envoyé ailleurs qu'au serveur Jellyfin.
(function () {
    'use strict';
    if (window.__abrOverlay) { return; }
    window.__abrOverlay = true;

    var MAX = 120;                 // requêtes gardées (playlists et segments)
    var entries = [];
    var info = null, texts = {}, denied = false, closed = false, lastFetch = 0, panel = null;
    function tr(s) { return texts[s] || s; }
    function trf(s) { var a = arguments; return tr(s).replace(/\{(\d+)\}/g, function (m, i) { return String(a[+i + 1]); }); }
    var lang = (document.documentElement.lang || navigator.language || 'en').slice(0, 2).toLowerCase();
    function num(x, d) { var s = (Math.round(x * Math.pow(10, d)) / Math.pow(10, d)).toFixed(d); return lang === 'en' ? s : s.replace('.', ','); }

    function param(url, name) {
        var m = new RegExp('[?&]' + name + '=([^&#]*)', 'i').exec(url);
        return m ? decodeURIComponent(m[1]) : null;
    }

    // requêtes du lecteur : playlists des qualités (main.m3u8) et segments (hls1/…)
    function keep(e) {
        var n = e.name || '';
        if (n.indexOf('/main.m3u8') < 0 && n.indexOf('/hls1/') < 0) { return; }
        entries.push({
            url: n,
            playlist: n.indexOf('/main.m3u8') >= 0,
            size: e.transferSize || e.encodedBodySize || 0,
            // temps de transfert seulement (le serveur peut attendre l'encodeur avant le premier octet)
            ms: Math.max(1, (e.responseEnd || 0) - (e.responseStart || e.startTime || 0)),
            at: (performance.timeOrigin || 0) + (e.responseEnd || 0),
        });
        if (entries.length > MAX) { entries.splice(0, entries.length - MAX); }
    }
    try {
        new PerformanceObserver(function (l) { l.getEntries().forEach(keep); }).observe({ type: 'resource', buffered: true });
        if (performance.setResourceTimingBufferSize) { performance.setResourceTimingBufferSize(1000); }
        performance.addEventListener && performance.addEventListener('resourcetimingbufferfull', function () { performance.clearResourceTimings(); });
    } catch (e) { /* navigateur sans Resource Timing : la surcouche affiche ce qu'elle peut */ }

    function video() {
        var v = document.querySelector('video.htmlvideoplayer') || document.querySelector('.videoPlayerContainer video') || document.querySelector('video');
        return v && !v.ended && v.readyState > 0 && v.offsetParent !== null ? v : null;
    }

    function state() {
        var lists = entries.filter(function (e) { return e.playlist; });
        var segs = entries.filter(function (e) { return !e.playlist && e.size > 0; });
        var cur = lists.length ? lists[lists.length - 1].url : null;
        var rate = function (e) { return e.size * 8 / (e.ms / 1000) / 1e6; };
        var last5 = segs.slice(-5);
        return {
            session: cur ? param(cur, 'PlaySessionId') : null,
            kbps: cur ? parseInt(param(cur, 'VideoBitrate') || '0', 10) / 1000 : null,
            copy: cur ? param(cur, 'AllowVideoStreamCopy') !== 'false' : false,
            lastRate: segs.length ? rate(segs[segs.length - 1]) : null,
            avgRate: last5.length ? last5.reduce(function (s, e) { return s + e.size * 8; }, 0) / (last5.reduce(function (s, e) { return s + e.ms; }, 0) / 1000) / 1e6 : null,
        };
    }

    function fetchInfo(session) {
        if (denied || typeof ApiClient === 'undefined' || Date.now() - lastFetch < 3000) { return; }
        lastFetch = Date.now();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('AdaptiveBitrate/Session', { playSessionId: session || '', lang: lang }), dataType: 'json' })
            .then(function (r) { texts = r.texts || {}; info = r.session; }, function (e) { if (e && e.status === 403) { denied = true; hide(); } });
    }

    function build() {
        panel = document.createElement('div');
        panel.className = 'playerStats abr-overlay';
        panel.setAttribute('style', 'position:fixed;top:4.5em;left:1em;z-index:10000;max-width:30em;padding:.6em .9em;border-radius:6px;'
            + 'background:rgba(0,0,0,.72);color:#fff;font-size:.82em;line-height:1.45;pointer-events:auto;');
        var head = document.createElement('div');
        head.setAttribute('style', 'display:flex;justify-content:space-between;gap:1em;font-weight:600;margin-bottom:.3em;');
        var title = document.createElement('span');
        title.className = 'abr-title';
        var x = document.createElement('button');
        x.type = 'button';
        x.textContent = '×';
        x.setAttribute('aria-label', 'close');
        x.setAttribute('style', 'background:none;border:0;color:inherit;font-size:1.3em;line-height:1;cursor:pointer;padding:0;');
        x.addEventListener('click', function () { closed = true; hide(); });
        head.appendChild(title);
        head.appendChild(x);
        panel.appendChild(head);
        var body = document.createElement('div');
        body.className = 'abr-body';
        panel.appendChild(body);
        document.body.appendChild(panel);
    }

    function hide() { if (panel) { panel.style.display = 'none'; } }

    function line(body, label, value) {
        var d = document.createElement('div');
        var l = document.createElement('span');
        l.setAttribute('style', 'opacity:.7;');
        l.textContent = label + (lang === 'fr' ? ' : ' : ': ');
        var v = document.createElement('span');
        v.textContent = value;
        d.appendChild(l);
        d.appendChild(v);
        body.appendChild(d);
    }

    function render(v) {
        if (!panel) { build(); }
        panel.style.display = '';
        panel.querySelector('.abr-title').textContent = tr('Adaptive Bitrate · diagnostic');
        var body = panel.querySelector('.abr-body');
        body.textContent = '';
        var s = state();
        if (!s.session) {
            line(body, tr('Flux'), tr('pas de flux HLS (lecture directe, ou flux sans échelle)'));
        } else {
            var offers = info && info.offers ? info.offers : [];
            var curLabel = s.copy && !(s.kbps > 0 && offers.length && s.kbps * 1000 < offers[0].kbps * 1000 * 0.95)
                ? tr('origine (vidéo copiée)') : trf('{0} Mb/s', num((s.kbps || 0) / 1000, 2));
            var idx = -1;
            offers.forEach(function (o, i) { if (idx < 0 && s.kbps > 0 && Math.abs(o.kbps - s.kbps) / o.kbps < 0.25) { idx = i; } });
            if (s.copy && idx < 0 && offers.length) { idx = 0; }
            line(body, tr('Qualité téléchargée'), curLabel + (idx >= 0 ? ' · ' + trf('{0} sur {1}', idx + 1, offers.length) + (offers[idx].resolution ? ' · ' + offers[idx].resolution : '') : ''));
            line(body, tr('Débit reçu'), s.lastRate === null ? '—' : trf('{0} Mb/s (dernier segment) · {1} Mb/s (5 derniers)', num(s.lastRate, 2), num(s.avgRate, 2)));
            if (offers.length) { line(body, tr('Qualités proposées'), offers.map(function (o) { return num(o.kbps / 1000, 2); }).join(' · ') + ' Mb/s'); }
            if (info && info.steps && info.steps.length) {
                var steps = info.steps.slice(-5).map(function (p) {
                    var t = new Date(p.at);
                    return t.toLocaleTimeString() + ' → ' + (p.kbps > 0 ? num(p.kbps / 1000, 2) : tr('origine'));
                });
                line(body, trf('Changements ({0} descentes, {1} montées)', info.down, info.up), steps.join(' ; '));
            }
        }
        if (info && info.server) {
            var m = info.server;
            line(body, tr('Mesure du serveur'), (m.rateKbps === null ? '—' : trf('{0} Mb/s livrés au lecteur', num(m.rateKbps / 1000, 2)))
                + (m.lead === null ? '' : ' · ' + trf('avance du lecteur {0} s', num(m.lead, 1))));
            if (typeof m.encoderWait === 'number' && m.encoderWait >= 0.05) {
                line(body, tr('Encodage'), trf('{0} s d\'attente par segment de {1} s', num(m.encoderWait, 2), num(m.segment, 1)));
            }
            if (typeof m.margin === 'number') {
                line(body, tr('Réglages appliqués'), trf('marge {0} · écart {1} s · {2} segments', num(m.margin, 2), num(m.minChange, 1), m.minSamples)
                    + (typeof m.startup === 'number' ? ' · ' + trf('démarrage d\'un encodage {0} s', num(m.startup, 1)) : ''));
            }
            if (m.held || m.anticipated) { line(body, tr('Décisions du serveur'), trf('{0} remontées retenues, {1} descentes anticipées', m.held, m.anticipated)); }
        }
        var ahead = 0;
        for (var i = 0; i < v.buffered.length; i++) { if (v.buffered.start(i) <= v.currentTime + 0.1 && v.buffered.end(i) > v.currentTime) { ahead = v.buffered.end(i) - v.currentTime; } }
        line(body, tr('Tampon'), trf('{0} s d\'avance', num(ahead, 1)));
        line(body, tr('Image affichée'), v.videoWidth + '×' + v.videoHeight);
        var q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null;
        if (q) { line(body, tr('Images perdues'), q.droppedVideoFrames + ' / ' + q.totalVideoFrames); }
        fetchInfo(s.session);
    }

    setInterval(function () {
        if (denied || closed) { return; }
        var v = video();
        if (!v) { hide(); return; }
        render(v);
    }, 1000);
})();
