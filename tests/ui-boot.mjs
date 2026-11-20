import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

// The page loads six classic scripts into one global scope, so a top-level name in one of them
// can collide with a name in another and stop a script from running at all. Only a boot with the
// real script set, SignalR build included, can see that, which is why this fixture stubs the API
// rather than the scripts.
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const root = fileURLToPath(new URL('../wwwroot/', import.meta.url));
const mime = new Map([['.html', 'text/html'], ['.js', 'text/javascript'], ['.css', 'text/css'],
  ['.svg', 'image/svg+xml'], ['.woff2', 'font/woff2']]);
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || 'chromium',
  args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage'] });
try {
  const page = await browser.newPage({viewport: {width: 900, height: 900}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.route('http://boot.test/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/bell/negotiate') return route.fulfill({status: 503, body: '' });
    if (url.pathname === '/api/duels') return route.fulfill({contentType: 'application/json',
      body: JSON.stringify({id: 'boot', side: 'defender', seatToken: 'boot-seat'}) });
    if (url.pathname.startsWith('/api/')) return route.fulfill({contentType: 'application/json', body: '{}' });
    const file = url.pathname === '/' ? '/index.html' : url.pathname;
    const body = await readFile(root + file.slice(1));
    const type = mime.get(file.slice(file.lastIndexOf('.'))) ?? 'application/octet-stream';
    await route.fulfill({contentType: type, body});
  });

  await page.goto('http://boot.test/');
  await page.waitForTimeout(600);
  assert.deepEqual(errors, [], 'the real script set must load without an error');
  // A lexical global is declared but holds undefined until assigned, so the probe names one the
  // file assigns at load: `tile` is game.js's own number
  await page.waitForFunction(() => typeof tile === 'number' && typeof i18n === 'object' &&
    typeof sounds === 'object' && typeof Tower === 'object', undefined, {timeout: 8000});
  assert.equal(await page.locator('#auth').isVisible(), true, 'the first screen is the sign-in card');
  assert.deepEqual(errors, [], 'the whole script set runs without an error');
  console.log('PASS: all six classic scripts run in one scope, with the real SignalR build loaded');
} finally {
  await browser.close();
}
