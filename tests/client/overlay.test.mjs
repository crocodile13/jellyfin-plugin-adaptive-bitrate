// Tests de la surcouche de diagnostic dans un DOM simulé : cd tests/client && npm install --no-save jsdom@24.1.3 && node overlay.test.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const here = dirname(fileURLToPath(import.meta.url));
const JS = readFileSync(join(here, '../../src/Jellyfin.Plugin.AdaptiveBitrate/Web/overlay.js'), 'utf8');
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
async function test(name, fn) {
    try { await fn(); console.log('ok   ' + name); } catch (e) { failures++; console.log('ÉCHEC ' + name + '\n     ' + (e.stack || e)); }
}

const S = 'abc123';
const OFFERS = [{ kbps: 8108, resolution: '1920x1080' }, { kbps: 4508, resolution: '1280x720' }, { kbps: 2546, resolution: '1202x676' }, { kbps: 1475, resolution: '874x491' }, { kbps: 892, resolution: '640x360' }];

function page(opts) {
    const dom = new JSDOM('<!doctype html><html lang="' + (opts.lang || 'fr') + '"><body><div class="videoPlayerContainer"><video class="htmlvideoplayer"></video></div></body></html>',
        { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const v = w.document.querySelector('video');
    for (const [k, val] of Object.entries({ readyState: 4, ended: false, videoWidth: 1280, videoHeight: 720, currentTime: 10, offsetParent: w.document.body })) {
        Object.defineProperty(v, k, { get: () => val });
    }
    Object.defineProperty(v, 'buffered', { get: () => ({ length: 1, start: () => 0, end: () => 16.4 }) });
    v.getVideoPlaybackQuality = () => ({ droppedVideoFrames: 3, totalVideoFrames: 2410 });
    // requêtes du lecteur (Resource Timing) : la playlist de la qualité 4,5 Mb/s puis deux segments
    const entries = opts.entries || [
        { name: `http://jf/videos/x/main.m3u8?PlaySessionId=${S}&VideoBitrate=4300000&AllowVideoStreamCopy=false`, transferSize: 900, responseStart: 10, responseEnd: 12 },
        { name: 'http://jf/videos/x/hls1/main/5.ts', transferSize: 1_500_000, responseStart: 100, responseEnd: 2100 },
        { name: 'http://jf/videos/x/hls1/main/6.ts', transferSize: 1_500_000, responseStart: 3000, responseEnd: 4000 },
    ];
    w.PerformanceObserver = class { constructor(cb) { this.cb = cb; } observe() { this.cb({ getEntries: () => entries }); } };
    const calls = [];
    w.ApiClient = {
        getUrl: (p, q) => '/' + p + '?' + new URLSearchParams(q),
        ajax: (o) => {
            calls.push(o.url);
            if (opts.denied) { return Promise.reject({ status: 403 }); }
            return Promise.resolve({ texts: opts.texts || {}, session: { title: '<b>x</b>', offers: OFFERS, steps: [{ at: '2026-11-27T20:00:00Z', kbps: 0 }, { at: '2026-11-27T20:00:40Z', kbps: 4300 }], down: 1, up: 0,
                server: opts.server === undefined ? { rateKbps: 9100, lead: 14.2, held: 0, anticipated: 0, margin: 1.32, minChange: 7.5, minSamples: 2, startup: 1.4, encoderWait: 1.2, segment: 3 } : opts.server } });
        },
    };
    w.eval(JS);
    return { w, d: w.document, calls };
}

await test('surcouche : qualité en cours, débit reçu, qualités proposées, tampon, image, images perdues', async () => {
    const { d, calls } = page({});
    await wait(1200);
    await wait(1100);
    const p = d.querySelector('.abr-overlay');
    assert.ok(p, 'surcouche affichée');
    const text = p.textContent;
    assert.match(text, /Qualité téléchargée : 4,30 Mb\/s · 2 sur 5 · 1280x720/);
    assert.match(text, /Débit reçu : 12,00 Mb\/s \(dernier segment\) · 8,00 Mb\/s \(5 derniers\)/);
    assert.match(text, /8,11 · 4,51 · 2,55 · 1,48 · 0,89 Mb\/s/);
    assert.match(text, /Changements \(1 descentes, 0 montées\)/);
    assert.match(text, /Tampon : 6,4 s d'avance/);
    assert.match(text, /Image affichée : 1280×720/);
    assert.match(text, /Images perdues : 3 \/ 2410/);
    assert.match(text, /Mesure du serveur : 9,10 Mb\/s livrés au lecteur · avance du lecteur 14,2 s/);
    assert.match(text, /Encodage : 1,20 s d'attente par segment de 3,0 s/);
    assert.match(text, /Réglages appliqués : marge 1,32 · écart 7,5 s · 2 segments · démarrage d'un encodage 1,4 s/);
    assert.ok(calls.some((u) => u.includes('playSessionId=' + S)), 'la lecture demandée au serveur est celle du lecteur');
    assert.equal(p.querySelector('b'), null, 'rien n\'est inséré comme du HTML');
});

await test('refusée par le serveur (403) : rien ne s\'affiche', async () => {
    const { d } = page({ denied: true });
    await wait(2300);
    const p = d.querySelector('.abr-overlay');
    assert.ok(!p || p.style.display === 'none');
});

await test('lecture directe (aucune playlist HLS) : le dit, sans inventer de qualité', async () => {
    const { d } = page({ entries: [] });
    await wait(1200);
    assert.match(d.querySelector('.abr-overlay').textContent, /pas de flux HLS/);
});

await test('fermée avec × : ne revient pas pendant la lecture', async () => {
    const { d } = page({});
    await wait(1200);
    d.querySelector('.abr-overlay button').click();
    await wait(1200);
    assert.equal(d.querySelector('.abr-overlay').style.display, 'none');
});

await test('en anglais : textes du serveur, point décimal', async () => {
    const EN = JSON.parse(readFileSync(join(here, '../../src/AdaptiveBitrate.Core/english.json'), 'utf8'));
    const { d } = page({ lang: 'en', texts: EN });
    await wait(1200);
    await wait(1100);
    assert.match(d.querySelector('.abr-overlay').textContent, /Quality downloaded: 4\.30 Mb\/s · 2 of 5/);
});

console.log(failures ? failures + ' test(s) en échec' : '\ntous les tests passent');
process.exit(failures ? 1 : 0);   // la surcouche tourne en continu (minuterie) : sortie explicite
