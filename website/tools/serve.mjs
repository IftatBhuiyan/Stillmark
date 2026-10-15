import http from 'node:http';
import { readFile, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = await realpath(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'));
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.png': 'image/png', '.webp': 'image/webp', '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
  '.woff2': 'font/woff2', '.txt': 'text/plain; charset=utf-8' };

http.createServer(async (request, response) => {
  response.setHeader('X-Content-Type-Options', 'nosniff');
  response.setHeader('Cache-Control', 'no-store');
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    response.writeHead(405, { Allow: 'GET, HEAD' }).end(); return;
  }
  try {
    let relative = decodeURIComponent(new URL(request.url, 'http://127.0.0.1:8765').pathname).slice(1);
    if (!relative) relative = 'index.html';
    const allowlist = (await readFile(path.join(root, 'tools/publish-allowlist.txt'), 'utf8'))
      .split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('#'));
    if (!allowlist.includes(relative)) { response.writeHead(404).end('Not found'); return; }
    const file = await realpath(path.join(root, relative));
    if (!file.startsWith(root + path.sep)) { response.writeHead(404).end('Not found'); return; }
    const bytes = await readFile(file);
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream',
      'Content-Length': bytes.length });
    response.end(request.method === 'HEAD' ? undefined : bytes);
  } catch {
    response.writeHead(404).end('Not found');
  }
}).listen(8765, '127.0.0.1', () => console.log('Stillmark preview: http://127.0.0.1:8765/'));
