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
const autoplay = process.env.AUDIO_AUTOPLAY === '1';
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || 'chromium',
  args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage',
    `--autoplay-policy=${autoplay ? 'no-user-gesture-required' : 'document-user-activation-required'}`] });
try {
  const page = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  const errors = [];
  const joins = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  async function serve(route) {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/duels/invite/join') {
      joins.push(route.request().postData());
      return route.fulfill({status: 409, json: {detail: 'Место занято'}});
    }
    if (url.pathname === '/bell/negotiate') return route.fulfill({status: 503, body: '' });
    if (url.pathname === '/api/duels') return route.fulfill({contentType: 'application/json',
      body: JSON.stringify({id: 'boot', side: 'defender', seatToken: 'boot-seat'}) });
    if (url.pathname.startsWith('/api/')) return route.fulfill({contentType: 'application/json', body: '{}' });
    const file = url.pathname === '/' ? '/index.html' : url.pathname;
    const body = await readFile(root + file.slice(1));
    const type = mime.get(file.slice(file.lastIndexOf('.'))) ?? 'application/octet-stream';
    await route.fulfill({contentType: type, body});
  }
  await page.route('http://boot.test/**', serve);

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

  await page.goto('http://boot.test/?room=invite');
  const join = page.getByRole('button', {name: 'Вступить в дуэль'});
  await join.waitFor({timeout: 5000});
  assert.equal(await page.locator('#auth input[type="radio"]').count(), 0, 'No side picker on the invite screen');
  await join.click();
  await page.locator('#authError').getByText(/Место занято/).waitFor({timeout: 5000});
  assert.deepEqual(joins, [null], 'Ask the server for the free seat with one request and no side');
  console.log('PASS: the invite screen delegates seat choice to the server');
  await page.close();

  for (const [languages, expected] of [
    [['en-GB', 'ru-RU'], 'en'],
    [['de-DE', 'ru-RU', 'en-GB'], 'ru'],
    [['fr-CA'], 'en'],
  ]) {
    const visit = await browser.newPage();
    await visit.route('http://boot.test/**', serve);
    await visit.addInitScript(languages => {
      Object.defineProperties(navigator, {
        languages: { value: languages },
        language: { value: languages[0] },
      });
    }, languages);
    await visit.goto('http://boot.test/');
    const lang = () => visit.locator('html').getAttribute('lang');
    const saved = () => visit.evaluate(() => localStorage.getItem('truck-kun-tower:lang'));
    assert.equal(await lang(), expected, languages.join(', '));
    assert.equal(await saved(), null, 'A browser default is not a manual choice');
    await visit.getByRole('button', {name: expected.toUpperCase(), exact: true}).click();
    assert.equal(await saved(), expected, 'Clicking the active language also saves it');
    const other = expected === 'en' ? 'ru' : 'en';
    await visit.getByRole('button', {name: other.toUpperCase(), exact: true}).click();
    await visit.reload();
    assert.equal(await lang(), other, 'The saved language wins on reload');
    await visit.close();
  }
  console.log('PASS: language follows browser preferences until a manual choice');

  const viewer = await browser.newPage({locale: 'en-GB'});
  await viewer.addInitScript(() => {
    const create = AudioContext.prototype.createOscillator;
    window.audibleCues = 0;
    AudioContext.prototype.createOscillator = function () {
      if (this.state === 'running') window.audibleCues++;
      return create.call(this);
    };
  });
  const requested = [];
  const replayErrors = [];
  viewer.on('pageerror', error => replayErrors.push(error.message));
  await viewer.route('http://boot.test/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (!path.startsWith('/api/') && !path.startsWith('/bell')) return serve(route);
    requested.push(path);
    assert.equal(route.request().headers().authorization, undefined);
    if (path === '/api/matches/test/replay') return route.fulfill({json: {
      width: 3, height: 3, paths: [[0, 1, 4]], alternatePaths: [], waveCount: 2,
    }});
    const wave = Number(path.split('/').at(-1));
    if ([1, 2].includes(wave)) return route.fulfill({json: {
      replayMs: 700, replayStart: {hp: 10, buildings: [], fences: []},
      frames: [{hp: 10 - wave, buildings: [], fences: [], mobs: [], shots: [], aims: [], siege: []}],
    }});
    return route.fulfill({status: 404, json: {status: 404}});
  });
  await viewer.goto('http://boot.test/?replay=test');
  await viewer.locator('#hp').getByText('8', {exact: true}).waitFor({timeout: 5000});
  assert.deepEqual(requested, ['/api/matches/test/replay', '/api/matches/test/replay/1', '/api/matches/test/replay/2']);
  assert.equal(await viewer.locator('#auth').isHidden(), true);
  assert.equal(await viewer.locator('#cells button').count(), 0);
  const sound = viewer.locator('#sound');
  if (autoplay) {
    assert.ok(await viewer.evaluate(() => window.audibleCues > 0), 'Allowed audio starts with the replay');
  } else {
    assert.equal(await viewer.evaluate(() => window.audibleCues), 0);
    assert.equal(await sound.getAttribute('aria-pressed'), 'false');
    await viewer.getByRole('button', {name: 'Enable sound', exact: true}).click({timeout: 2000});
    await viewer.waitForFunction(() => document.querySelector('#sound').getAttribute('aria-pressed') === 'true');
  }
  await viewer.waitForFunction(() => !replay, undefined, {timeout: 5000});
  const unlockCues = await viewer.evaluate(() => window.audibleCues);
  await viewer.getByRole('button', {name: 'Restart this wave'}).click();
  await viewer.waitForFunction(count => window.audibleCues > count, unlockCues);
  await viewer.getByLabel('Choose a wave').selectOption('1');
  await viewer.locator('#hp').getByText('9', {exact: true}).waitFor({timeout: 5000});
  await viewer.goto('http://boot.test/?replay=missing');
  await viewer.getByRole('heading', {name: 'Match replay is not available'}).waitFor({timeout: 5000});
  assert.deepEqual(replayErrors, []);
  await viewer.close();
  console.log('PASS: replays play every wave in order, can jump to a wave, and do not join a room');
} finally {
  await browser.close();
}
