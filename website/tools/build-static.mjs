import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import './validate-site.mjs';

if (process.exitCode) process.exit(process.exitCode);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.resolve(root, '../dist/site');
fs.mkdirSync(output, { recursive: true });
for (const name of fs.readdirSync(root)) {
  if (!/\.(html|css)$/.test(name) && !['site.js', 'robots.txt', '_headers', '_redirects'].includes(name)) continue;
  fs.copyFileSync(path.join(root, name), path.join(output, name));
}
fs.cpSync(path.join(root, 'assets'), path.join(output, 'assets'), { recursive: true });
console.log(`Static site: ${output}`);
