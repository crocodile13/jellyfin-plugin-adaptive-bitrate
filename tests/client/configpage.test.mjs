// Tests de la page de configuration dans un DOM simulé : cd tests/client && npm install --no-save jsdom@24.1.3 && node configpage.test.mjs
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '../..');
const HTML = readFileSync(join(root, 'src/Jellyfin.Plugin.AdaptiveBitrate/Web/configPage.html'), 'utf8');
const EN = JSON.parse(readFileSync(join(root, 'src/AdaptiveBitrate.Core/english.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
async function test(name, fn) {
    try { await fn(); console.log('ok   ' + name); } catch (e) { failures++; console.log('ÉCHEC ' + name + '\n     ' + (e.stack || e)); }
}

const CFG = { Enabled: true, Scope: 'Remote', MaxRungs: 4, Step: 0.6, MinBitrateKbps: 700, RungsAbove: 0, AboveCapKbps: 0, PreferAdaptive: false, ExcludedClients: '', Language: 'System' };
const STATUS = { jellyfin: '12.4.0.0', tested: true, compatible: true, sessions: [
    { title: 'Film <b>gras</b>', client: 'Jellyfin Web', offered: 5, down: 2, up: 1, steps: [{ kbps: 0 }, { kbps: 2880 }, { kbps: 1728 }, { kbps: 4800 }],
      server: { rateKbps: 9100, held: 1, anticipated: 0, margin: 1.32, minChange: 7.5, minSamples: 2 } }], startup: 1.4 };

async function open(cfg, opts) {
    const dom = new JSDOM('<!doctype html><html lang="' + ((opts && opts.lang) || 'fr') + '"><body>' + HTML.replace(/^[\s\S]*<body>|<\/body>[\s\S]*$/g, '') + '</body></html>',
        { runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const saved = [];
    w.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} };
    w.ApiClient = {
        getUrl: (p, q) => '/' + p + (q ? '?' + new URLSearchParams(q) : ''),
        getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(cfg))),
        updatePluginConfiguration: (id, c) => { saved.push(JSON.parse(JSON.stringify(c))); return Promise.resolve({}); },
        ajax: (o) => {
            const u = o.url.split('?')[0];
            if (u === '/AdaptiveBitrate/I18n') { return Promise.resolve((opts && opts.i18n) || { lang: 'fr', dict: {} }); }
            if (u === '/AdaptiveBitrate/Status') { return Promise.resolve((opts && opts.status) || STATUS); }
            return Promise.reject(new Error('inattendu : ' + u));
        },
    };
    for (const s of w.document.querySelectorAll('script')) { w.eval(s.textContent); }
    w.document.getElementById('AdaptiveBitrateConfigPage').dispatchEvent(new w.Event('pageshow'));
    await wait(30);
    return { w, d: w.document, saved };
}

await test('réglages lus, état et lectures récentes affichés (texte brut, rien d\'injecté)', async () => {
    const { d } = await open(CFG);
    assert.equal(d.getElementById('abrEnabled').checked, true);
    assert.equal(d.getElementById('abrScope').value, 'Remote');
    assert.equal(d.getElementById('abrMaxRungs').value, '4');
    assert.match(d.getElementById('abrHealth').textContent, /12\.4\.0\.0 : testé/);
    const cells = [...d.querySelectorAll('#abrSessions td')].map((c) => c.textContent);
    assert.ok(cells.includes('Film <b>gras</b>'), 'titre affiché tel quel');
    assert.equal(d.querySelector('#abrSessions b'), null);
    assert.ok(cells.includes('origine → 2880 → 1728 → 4800'));
    assert.ok(cells.includes('9100 kb/s, 1 remontées retenues, 0 descentes anticipées'));
    assert.ok(cells.includes('marge 1,32 · écart 7,5 s · 2 segments'), 'valeurs réellement appliquées (automatiques)');
    assert.match(d.getElementById('abrHealth').textContent, /Démarrage d'un encodage sur ce serveur : 1,4 s/);
});

await test('réglage sans effet : grisé avec la raison ; plugin coupé : tout grisé ; valeurs gardées', async () => {
    const { w, d } = await open(CFG);
    const cap = d.getElementById('abrAboveCapKbps');
    assert.equal(cap.disabled, true);
    assert.match(cap.closest('[data-key]').querySelector('.abr-why').textContent, /aucune qualité plus haute proposée/);
    d.getElementById('abrRungsAbove').value = '1';
    d.getElementById('abrRungsAbove').dispatchEvent(new w.Event('input'));
    assert.equal(cap.disabled, false);
    d.getElementById('abrEnabled').checked = false;
    d.getElementById('abrEnabled').dispatchEvent(new w.Event('change'));
    assert.equal(d.getElementById('abrMaxRungs').disabled, true);
    assert.match(d.getElementById('abrMaxRungs').closest('[data-key]').querySelector('.abr-why').textContent, /plugin coupé/);
    assert.equal(d.getElementById('abrMaxRungs').value, '4');
});

await test('enregistrer : chaque champ revient dans la configuration (nombres, applications exclues)', async () => {
    const { w, d, saved } = await open(CFG);
    d.getElementById('abrScope').value = 'All';
    d.getElementById('abrStep').value = '0.7';
    d.getElementById('abrPreferAdaptive').checked = true;
    d.getElementById('abrExcludedClients').value = 'Kodi\nRoku';
    d.getElementById('abrForm').dispatchEvent(new w.Event('submit', { cancelable: true }));
    await wait(30);
    assert.equal(saved.length, 1);
    assert.deepEqual({ Scope: saved[0].Scope, Step: saved[0].Step, PreferAdaptive: saved[0].PreferAdaptive, ExcludedClients: saved[0].ExcludedClients, MaxRungs: saved[0].MaxRungs },
        { Scope: 'All', Step: 0.7, PreferAdaptive: true, ExcludedClients: 'Kodi\nRoku', MaxRungs: 4 });
});

await test('en anglais : textes du serveur, rien en français', async () => {
    const { d } = await open(CFG, { lang: 'en', i18n: { lang: 'en', dict: EN } });
    assert.equal(d.querySelector('[data-t="Enregistrer"]').textContent, 'Save');
    assert.match(d.getElementById('abrHealth').textContent, /12\.4\.0\.0: tested/);
    const french = [...d.querySelectorAll('[data-t]')].filter((e) => e.textContent === e.getAttribute('data-t') && /[éèàç]/.test(e.textContent));
    assert.deepEqual(french.map((e) => e.textContent), []);
});

await test('Jellyfin incompatible : dit clairement que la playlist n\'est pas modifiée', async () => {
    const { d } = await open(CFG, { status: { ...STATUS, compatible: false, sessions: [] } });
    assert.match(d.getElementById('abrHealth').textContent, /ne la modifie pas/);
    assert.match(d.getElementById('abrSessions').textContent, /Aucune lecture/);
});

console.log(failures ? failures + ' test(s) en échec' : '\ntous les tests passent');
process.exitCode = failures ? 1 : 0;
