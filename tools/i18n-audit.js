// Run from the repo root: node tools/i18n-audit.js
//
// Every @T["key"] a view asks for, against what the resources actually contain — for both apps and
// both cultures. A missing key does not fail the build; it renders as the key itself, which is how
// "instances.filterAllInstances" ended up on screen.
const fs = require('fs');
const path = require('path');

function usedKeys(dir) {
    const used = new Set();
    (function walk(d) {
        for (const e of fs.readdirSync(d, { withFileTypes: true })) {
            if (['bin', 'obj', 'node_modules'].includes(e.name)) continue;
            const p = path.join(d, e.name);
            if (e.isDirectory()) walk(p);
            else if (e.name.endsWith('.cshtml') || e.name.endsWith('.cs')) {
                const s = fs.readFileSync(p, 'utf8');
                for (const m of s.matchAll(/@?T\["([^"]+)"\]/g)) used.add(m[1]);
            }
        }
    })(dir);
    return used;
}

let bad = 0;
for (const app of ['src/MatCMS', 'src/MatCMS.Cloud']) {
    const used = usedKeys(app + '/Pages');
    // de/en only — deliberately, and NOT because hr/sk went away: their Resources/*.json stay on disk
    // so re-offering them later is a translation, not an archaeology. They just hold ~19 of ~1180 keys,
    // so auditing them would report eight hundred "missing" and turn this gate permanently red for
    // everyone. Audit a language here once it is complete enough to be offered in the admin switcher
    // (Localizer.AdminUiCultures) — the two lists belong together.
    for (const culture of ['de', 'en']) {
        const res = JSON.parse(fs.readFileSync(app + '/Resources/' + culture + '.json', 'utf8'));
        const missing = [...used].filter(k => !(k in res)).sort();
        console.log(`${app}/${culture}: ${used.size} verwendet, ${missing.length} fehlen` +
            (missing.length ? '  -> ' + missing.join(', ') : ''));
        bad += missing.length;
    }
    // The other direction is only a hint, not a fault: a key can be used from code we did not scan.
    const de = JSON.parse(fs.readFileSync(app + '/Resources/de.json', 'utf8'));
    const en = JSON.parse(fs.readFileSync(app + '/Resources/en.json', 'utf8'));
    const onlyDe = Object.keys(de).filter(k => !(k in en));
    const onlyEn = Object.keys(en).filter(k => !(k in de));
    if (onlyDe.length || onlyEn.length)
        console.log(`  ${app}: nur de: ${onlyDe.join(', ') || '–'} | nur en: ${onlyEn.join(', ') || '–'}`);
}
process.exit(bad ? 1 : 0);
