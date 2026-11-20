import assert from 'node:assert/strict';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { extname, join } from 'node:path';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const root = new URL('../wwwroot/', import.meta.url);
const files = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css',
  '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer(async (req, res) => {
  const path = new URL(req.url, 'http://localhost').pathname.slice(1) || 'index.html';
  try {
    const type = files[extname(path)];
    if (!type) throw Error(path);
    const body = await readFile(new URL(path, root));
    res.writeHead(200, { 'Content-Type': type }).end(body);
  } catch {
    res.writeHead(404).end();
  }
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const origin = `http://127.0.0.1:${server.address().port}`;
const user = { id: 'u1', nickname: 'Илья {name}', createdAt: '2026-11-20T12:00:00Z' };
const opponent = { id: 'u2', nickname: 'Руна <guest>' };
const duel = { id: 'fixture', mySide: 'attacker', status: 'playing', opponent };
const board = {
  matchId: 'm1', seat: 1, gold: 20, done: false, hp: 12, joined: true, locked: false,
  last: 0, orders: [{ type: 'giant', cell: 3 }], towers: [], fences: [], frames: [], secondsLeft: 30,
  opponent: opponent.nickname, width: 5, height: 3, paths: [[3, 8, 13]],
  alternatePaths: [[3, 4, 9, 14, 13]], fenceCells: [],
  prices: { swarm: 1, giant: 2, smart: 3, runner: 4, armored: 5, decoy: 6, ranged: 7, apc: 8 },
};

let browser;
try {
  browser = await chromium.launch({ executablePath: process.env.CHROMIUM || 'chromium',
    args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage'] });
  for (const width of [320, 1280]) {
    const context = await browser.newContext({ viewport: { width, height: 900 },
      colorScheme: 'dark', timezoneId: 'UTC', permissions: ['clipboard-read', 'clipboard-write'] });
    const errors = [];
    let moves = 0;
    let registrationAttempts = 0;
    let current = structuredClone(board);
    // The fixture covers browser state and request shape, not SignalR transport or the database.
    await context.route('**/*', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (url.origin !== origin) { errors.push(`External request: ${url}`); return route.abort(); }
      const path = url.pathname;
      if (path === '/signalr.min.js') return route.fulfill({ contentType: 'text/javascript', body: `
        window.signalR = { HubConnectionBuilder: class {
          withUrl() { return this; } withAutomaticReconnect() { return this; }
          build() { return { state: 'Connected', on() {}, onreconnected() {}, onclose() {},
            async start() {}, async stop() {}, async invoke() {} }; }
        } };` });
      if (!path.startsWith('/api/')) return route.continue();
      const fixtures = {
        'POST /api/auth/register': { token: 'fixture-account', user },
        'GET /api/auth/me': user,
        'GET /api/friends': [{ ...opponent, online: true }],
        'GET /api/friends/requests': { incoming: [], outgoing: [{ id: 'r1', to: opponent }] },
        'GET /api/invites': [{ id: 'i1', roomId: duel.id, from: opponent }],
        'GET /api/duels': [duel],
        'POST /api/duels': { id: 'guest', seatToken: 'fixture-guest' },
        'GET /api/duels/fixture/state': current,
        'GET /api/duels/guest/state': { ...board, joined: false, orders: [] },
      };
      const key = `${request.method()} ${path}`;
      const account = request.headers().authorization;
      if (path === '/api/duels/guest/state' && (account || request.headers()['x-seat-token'] !== 'fixture-guest')) {
        errors.push('Wrong guest credentials');
      } else if (!['POST /api/auth/register', 'POST /api/duels', 'GET /api/duels/guest/state'].includes(key) &&
          account !== 'Bearer fixture-account') {
        errors.push(`Wrong account credentials for ${key}`);
      }
      if (key === 'POST /api/auth/register' && ++registrationAttempts === 1) {
        return route.fulfill({ status: 409, json: { status: 409 } });
      }
      if (key === 'POST /api/friends/requests') return route.fulfill({ status: 404,
        json: { status: 404, detail: 'Пользователь не найден' } });
      if (key === 'POST /api/duels/fixture/moves') {
        const move = request.postDataJSON();
        if (move.action === 'ready') {
          if (move.cell !== null || current.orders.length !== 0) errors.push(`Invalid skip: ${JSON.stringify(move)}`);
          current = { ...current, locked: true };
          return route.fulfill({ json: current });
        }
        if (move.wave !== 1 || move.action !== 'smart' || move.cell !== 3) errors.push(JSON.stringify(move));
        if (++moves === 1) return route.fulfill({ status: 409,
          json: { status: 409, detail: 'Этот ход недоступен' } });
        if (moves === 2) return route.fulfill({ status: 409, contentType: 'application/problem+json',
          body: JSON.stringify({ status: 409, code: 'purchase_limit', detail: 'Достигнут лимит покупок этого типа' }) });
        current = { ...current, gold: 17, orders: [...current.orders, { type: 'smart', cell: 3 }] };
        return route.fulfill({ json: current });
      }
      if (key in fixtures) return route.fulfill({ json: fixtures[key] });
      errors.push(`Unexpected API call: ${key}`);
      return route.fulfill({ status: 500, json: {} });
    });

    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.setDefaultTimeout(4000);
    await page.clock.install({ time: new Date('2026-11-21T12:00:00Z') });
    const button = name => page.getByRole('button', { name, exact: typeof name === 'string' });
    const visible = locator => locator.waitFor({ state: 'visible' });
    const github = () => page.getByRole('link', { name: /GitHub/ });
    const scheme = () => page.locator('html').evaluate(node => ({ mode: node.dataset.theme,
      ground: getComputedStyle(document.body).backgroundColor, lang: node.lang }));
    const storage = () => page.evaluate(() => ({ account: sessionStorage.getItem('tower:token'),
      guest: sessionStorage.getItem('duel:guest'), local: Object.keys(localStorage).sort() }));
    const shot = async name => {
      if (process.env.UI_OUTPUT) await page.screenshot({ fullPage: true,
        path: join(process.env.UI_OUTPUT, `${width}-${name}.png`) });
    };
    const alertStyle = locator => locator.evaluate(node => ({
      position: getComputedStyle(node).position, animation: getComputedStyle(node).animationName,
      masked: getComputedStyle(node, '::before').maskImage !== 'none',
    }));
    await page.goto(origin);
    await visible(page.getByRole('heading', { name: /грузовик/ }));
    assert.equal(await github().getAttribute('href'), 'https://github.com/rokokol/truck-kun-tower');
    const system = await scheme();
    assert.equal(system.mode, undefined);
    assert.equal(system.lang, 'ru');
    await button(/Тема: система/i).click();
    const light = await scheme();
    assert.equal(light.mode, 'light');
    assert.notEqual(light.ground, system.ground);
    await page.reload();
    assert.deepEqual(await scheme(), light);
    await button(/Тема: светлая/).click();
    assert.equal((await scheme()).mode, 'dark');
    await page.reload();
    assert.deepEqual(await scheme(), { ...system, mode: 'dark' });
    await button(/Тема: тёмная/).click();
    await page.emulateMedia({ colorScheme: 'light' });
    assert.deepEqual(await scheme(), { ...light, mode: undefined });
    await page.emulateMedia({ colorScheme: 'dark' });
    assert.deepEqual(await scheme(), system);
    await shot('ru-auth');

    await page.getByRole('tab', { name: 'Регистрация' }).click();
    await page.getByLabel('Ник', { exact: true }).fill(user.nickname);
    await page.getByLabel('Пароль', { exact: true }).fill('fixture-password');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await button('Зарегистрироваться').click();
    const authError = page.getByRole('alert').filter({ hasText: /Действие недоступно/ });
    await visible(authError.getByText('Ошибка', { exact: true }));
    assert.deepEqual(await alertStyle(authError), { position: 'relative', animation: 'none', masked: true });
    await button(/^Звук$/).click();
    await button('EN').click();
    assert.equal((await scheme()).lang, 'en');
    assert.equal(await page.getByRole('tab', { name: 'Register' }).getAttribute('aria-selected'), 'true');
    await visible(page.getByRole('alert').filter({ hasText: /Action unavailable/ }).getByText('Error', { exact: true }));
    assert.equal(await page.getByLabel('Nickname', { exact: true }).inputValue(), user.nickname);
    assert.equal(await page.getByLabel('Password', { exact: true }).inputValue(), 'fixture-password');
    assert.equal(await button(/^Sound$/).getAttribute('aria-pressed'), 'false');
    await visible(button('Theme: system'));
    assert.equal(await github().getAttribute('href'), 'https://github.com/rokokol/truck-kun-tower');
    assert.ok(await page.locator('header').evaluate(node => {
      const brand = node.querySelector('.brand').getBoundingClientRect();
      const controls = node.querySelector('.header-tools').getBoundingClientRect();
      return brand.bottom > controls.top && node.scrollWidth <= node.clientWidth;
    }), 'The brand and controls must fit on the same row');
    await shot('en-auth');
    await button('Register').click();
    await visible(page.getByRole('heading', { name: 'Lobby' }));
    await visible(page.getByText('November 20, 2026'));
    await visible(page.getByText(/Руна <guest> invites you/));
    await page.getByLabel('Add a friend by nickname').fill('Missing {name}');
    await button('Add').click();
    const lobbyError = page.getByRole('alert').filter({ hasText: /Not found.*Пользователь не найден/ });
    await visible(lobbyError.getByText('Error', { exact: true }));
    assert.deepEqual(await alertStyle(lobbyError), { position: 'relative', animation: 'none', masked: true });
    await button('RU').click();
    await visible(page.getByRole('heading', { name: 'Лобби' }));
    await visible(page.getByText(/20 ноября 2026/));
    await visible(page.getByRole('alert').filter({ hasText: /Не найдено.*Пользователь не найден/ }));
    assert.equal(await page.getByLabel('Добавить в друзья по нику').inputValue(), 'Missing {name}');
    assert.equal((await storage()).account, 'fixture-account');
    await shot('ru-lobby');
    await button('EN').click();
    await button('Theme: system').click();
    await page.reload();
    await visible(page.getByRole('heading', { name: 'Lobby' }));
    assert.deepEqual(await scheme(), { ...light, lang: 'en' });
    assert.equal(await button(/^Sound$/).getAttribute('aria-pressed'), 'false');
    await shot('en-lobby');

    await button('Continue').click();
    await visible(page.getByRole('heading', { name: /Wave 1 of 5.*planning/ }));
    await visible(page.getByText(`Opponent: ${opponent.nickname}`, { exact: true }));
    await button(/Smart slimes/).click();
    await button('Gate 1').click();
    await visible(page.getByText(/Gate 1.*shortest route/));
    await page.locator('#invite summary').click();
    await button('Copy link').click();
    await visible(button('Link copied'));
    await button('RU').click();
    await visible(page.getByRole('heading', { name: /Волна 1 из 5.*подготовка/ }));
    await visible(page.getByText(/Вход 1.*кратчайший путь/));
    await visible(page.getByText(/Гигант.*Вход 1/));
    await visible(button('Ссылка скопирована'));
    assert.equal(await button('Вход 1').getAttribute('aria-pressed'), 'true');
    assert.equal(await button(/Умные слизни/).getAttribute('aria-pressed'), 'true');
    assert.equal(await page.getByLabel('Ссылка для второго игрока').inputValue(), `${origin}/?room=fixture`);
    assert.equal(await page.evaluate(() => new URL(location.href).searchParams.get('room')), 'fixture');
    await shot('ru-game');
    await button(/Добавить в волну/).click();
    const gameError = page.getByRole('alert').filter({ hasText: /Этот ход недоступен/ });
    await visible(gameError.getByText('Ошибка', { exact: true }));
    assert.deepEqual(await alertStyle(gameError), { position: 'sticky', animation: 'none', masked: true });
    await button('EN').click();
    await visible(gameError.getByText('Error', { exact: true }));
    assert.equal(moves, 1);
    assert.equal(await button('Gate 1').getAttribute('aria-pressed'), 'true');
    await button(/Add to the wave/).click();
    const limitError = page.getByRole('alert').filter({ hasText: /Purchase limit/ });
    await visible(limitError);
    assert.doesNotMatch(await limitError.textContent(), /Достигнут/);
    await button('RU').click();
    await visible(page.getByRole('alert').filter({ hasText: /Достигнут лимит покупок этого типа/ }));
    await button('EN').click();
    await button(/Add to the wave/).click();
    await visible(page.getByText(/Smart slimes · Gate 1/));
    assert.equal(moves, 3);
    await gameError.waitFor({ state: 'hidden' });
    assert.equal((await storage()).account, 'fixture-account');
    await shot('en-game');

    const card = type => page.locator(`#actions [data-action=${type}]`);
    const pushState = async patch => {
      current = { ...current, ...patch };
      await page.evaluate(() => refresh());
    };
    assert.equal(await card('decoy').count(), 0);
    assert.doesNotMatch(await card('smart').innerText(), /\d+\/\d+/);
    await pushState({ limits: { smart: 1, giant: 3 } });
    assert.match(await card('smart').innerText(), /1\/1/);
    assert.match(await card('giant').innerText(), /1\/3/);
    assert.equal(await card('smart').isDisabled(), true);
    assert.equal(await card('giant').isEnabled(), true);
    await pushState({ orders: [] });
    assert.match(await card('smart').innerText(), /0\/1/);
    assert.equal(await card('smart').isEnabled(), true);
    await button('Skip wave').click();
    await visible(page.getByRole('heading', { name: /Waiting for your opponent/ }));

    await pushState({ seat: 0, locked: false, fenceCells: [4, 8, 9],
      prices: { bolt: 3, pulse: 4, fence: 3, spikes: 2, mine: 2 }, limits: { bolt: 1, pulse: 2, fence: 1 },
      towers: [{ type: 'bolt', cell: 2, hp: 5, max: 5 }], fences: [{ cell: 8, hp: 5, max: 5 }] });
    for (const type of ['bolt', 'fence']) {
      assert.match(await card(type).innerText(), /1\/1/);
      assert.equal(await card(type).isDisabled(), true);
    }
    assert.match(await card('pulse').innerText(), /0\/2/);
    for (const type of ['mine', 'spikes']) {
      assert.match(await card(type).innerText(), /One per route/);
      assert.equal(await card(type).isDisabled(), true, 'Missing trapCells must fail closed');
    }
    await pushState({ trapCells: [8, 9] });
    assert.equal(await card('mine').isEnabled(), true);
    await card('mine').click();
    assert.deepEqual(await page.locator('#cells button:visible').evaluateAll(nodes => nodes.map(n => n.dataset.cell)), ['9']);
    await page.locator('#cells [data-cell="9"]').click();
    assert.equal(await page.locator('#commit').isEnabled(), true);
    await button('RU').click();
    assert.match(await card('mine').innerText(), /Одна ловушка на маршрут/);
    await shot('ru-defender');
    await button('EN').click();
    await pushState({ trapCells: [] });
    assert.equal(await page.locator('#commit').isDisabled(), true);
    assert.equal(await card('mine').isDisabled(), true);

    const fresh = await context.newPage();
    fresh.on('pageerror', error => errors.push(error.message));
    await fresh.goto(origin);
    await visible(fresh.getByRole('button', { name: 'Play as guest' }));
    assert.equal(await fresh.evaluate(() => sessionStorage.length), 0);
    await fresh.getByRole('button', { name: 'Play as guest' }).click();
    await visible(fresh.getByRole('heading', { name: 'Waiting for the second player' }));
    await fresh.getByRole('button', { name: 'RU', exact: true }).click();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    const guest = await fresh.evaluate(() => sessionStorage.getItem('duel:guest'));
    assert.equal(guest, 'fixture-guest');
    await fresh.reload();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    const stored = await storage();
    assert.equal(stored.guest, null);
    assert.deepEqual(stored.local, ['truck-kun-tower:lang', 'truck-kun-tower:sound', 'truck-kun-tower:theme']);
    assert.deepEqual(errors, []);
    await context.close();
    console.log(`${width}px: preferences, auth, lobby, purchase limits, traps, skip, and guest isolation passed`);
  }
} finally {
  await browser?.close();
  server.close();
}
