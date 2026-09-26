// Generates a Markdown block reference from BlockRegistry.cs + de.json.
// Output: one section per built-in block with its fields (id, type, resolved German label).
// Text fields (Text/Textarea/RichText) are the ones an AI can set through the content-op channel;
// the rest are editor-only and marked accordingly.
const fs = require('fs');
const path = require('path');
const root = path.join(__dirname, '..', 'src', 'MatCMS');
const reg = fs.readFileSync(path.join(root, 'Content', 'BlockRegistry.cs'), 'utf8');
const de = JSON.parse(fs.readFileSync(path.join(root, 'Resources', 'de.json'), 'utf8').replace(/^﻿/, ''));
const T = k => de[k] || k;

// Split into block definitions. Each starts at `new BlockDefinition`.
const parts = reg.split(/new BlockDefinition\b/).slice(1);
const textTypes = new Set(['Text', 'Textarea', 'RichText']);
let out = [];
for (const chunk of parts) {
  const type = (chunk.match(/Type\s*=\s*"([^"]+)"/) || [])[1];
  if (!type) continue;
  const nameKey = (chunk.match(/Name\s*=\s*"([^"]+)"/) || [])[1] || '';
  // Only look at the Fields = [ ... ] region of THIS block (up to the first "Partial =" or end).
  const fieldsStart = chunk.indexOf('Fields');
  const region = fieldsStart >= 0 ? chunk.slice(fieldsStart) : chunk;
  const fieldRe = /Id\s*=\s*"([^"]+)"\s*,\s*Label\s*=\s*"([^"]*)"\s*,\s*Type\s*=\s*FieldType\.(\w+)/g;
  const fields = [];
  let m;
  while ((m = fieldRe.exec(region)) !== null) {
    fields.push({ id: m[1], label: T(m[2]), type: m[3] });
  }
  out.push({ type, name: T(nameKey), fields });
}

let md = '';
for (const b of out) {
  md += `\n### \`${b.type}\` — ${b.name}\n\n`;
  if (b.fields.length === 0) { md += '_(Container/keine direkten Felder)_\n'; continue; }
  md += '| Feld | Typ | KI-setzbar¹ | Bezeichnung |\n|---|---|:--:|---|\n';
  for (const f of b.fields) {
    const ai = textTypes.has(f.type) ? '✅' : '—';
    md += `| \`${f.id}\` | ${f.type} | ${ai} | ${f.label} |\n`;
  }
}
process.stdout.write(md);
