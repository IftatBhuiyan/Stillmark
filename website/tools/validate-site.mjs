import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pages = fs.readdirSync(root).filter(name => name.endsWith('.html'));
const failures = [];
let checkedLinks = 0;
for (const page of pages) {
  const source = fs.readFileSync(path.join(root, page), 'utf8');
  if (!source.includes('<main id="main"')) failures.push(`${page}: missing main landmark`);
  if (!/<title>[^<]+<\/title>/.test(source)) failures.push(`${page}: missing title`);
  if (/<form\b|sb_publishable_|supabase\.co|stripe\.com|account\.bundle|storefront\.bundle|\bbuy now\b|14.day free trial|\$30/i.test(source)) {
    failures.push(`${page}: retired commerce or activation surface`);
  }
  for (const [, target] of source.matchAll(/(?:href|src)="([^"]+)"/g)) {
    if (/^(https?:|mailto:|#|data:)/.test(target)) continue;
    const local = decodeURIComponent(target.split(/[?#]/)[0]);
    checkedLinks++;
    if (!fs.existsSync(path.join(root, local))) failures.push(`${page}: missing ${local}`);
  }
}
for (const file of ['site.js', 'open-source.css']) {
  const source = fs.readFileSync(path.join(root, file), 'utf8');
  if (/supabase|stripe|checkoutEnabled|sb_publishable_/i.test(source)) failures.push(`${file}: retired commerce integration`);
}
if (failures.length) {
  console.error(failures.join('\n'));
  process.exitCode = 1;
} else {
  console.log(`PASS: ${pages.length} pages, ${checkedLinks} local references, no active commerce/account integration.`);
}
