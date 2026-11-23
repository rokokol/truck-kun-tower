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
      locale: 'ru-RU', colorScheme: 'dark', timezoneId: 'UTC', permissions: ['clipboard-read', 'clipboard-write'] });
    const errors = [];
    let moves = 0;
    let searches = 0;
    let matchAvailable = false;
    let registrationAttempts = 0;
    let authMeFailure = false;
    let closureCode = null;
    let guestCreates = 0;
    const finishedGuestRooms = new Set();
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
      if (path === '/api/rules') return route.fulfill({json: {
        waveCount: 5,
        budgets: [
          {wave: 1, defender: 11, attacker: 16, bonusPerStructure: 0},
          {wave: 2, defender: 12, attacker: 17, bonusPerStructure: 1},
          {wave: 3, defender: 14, attacker: 20, bonusPerStructure: 1},
          {wave: 4, defender: 16, attacker: 24, bonusPerStructure: 2},
          {wave: 5, defender: 18, attacker: 29, bonusPerStructure: 0},
        ],
        units: [
          {type: 'swarm', hpFirst: 3, hpLast: 7, keepDamage: 1, fenceDamage: 1, radius: 0, interval: 0, stepTicks: 1.75},
          {type: 'armored', hpFirst: 7, hpLast: 11, keepDamage: 1, fenceDamage: 2, radius: 1, interval: 2, stepTicks: 2.5},
          {type: 'apc', hpFirst: 47, hpLast: 59, radius: 0, interval: 0, stepTicks: 4},
        ],
        structures: [{type: 'pulse', hpFirst: 24, hpLast: 24, damage: 1, radius: 2, interval: 3, maxTargets: 3}],
      }});
      const fixtures = {
        'POST /api/auth/register': { token: 'fixture-account', user },
        'GET /api/auth/me': user,
        'GET /api/friends': [{ ...opponent, online: true }],
        'GET /api/users/online': {count: 3, players: [{id: 'u3', nickname: 'Огненный слизень'}]},
        'GET /api/friends/requests': { incoming: [], outgoing: [{ id: 'r1', to: opponent }] },
        'GET /api/invites': [{ id: 'i1', roomId: duel.id, from: opponent }],
        'GET /api/duels': [duel, {id: 'closed', status: 'finished', replayId: 'm-old', opponent: {nickname: 'Побеждённый'}},
          {id: 'cancelled', status: 'cancelled', opponent: {nickname: 'Сбежавший'}},
          {id: 'duel-0', status: 'cancelled', startedAt: '2026-11-20T12:00:00Z'},
          {id: 'duel-1', status: 'cancelled', startedAt: null},
          {id: 'duel-2', status: 'finished', opponent: {nickname: 'Past duel 2'}},
          {id: 'duel-3', status: 'interrupted', opponent: {nickname: 'Past duel 3'}}],
        'GET /api/matches/mine': [{id: 'm-old', finishedAt: '2026-11-19T10:30:00Z', hasReplay: true,
          defender: user, attacker: opponent}, ...Array.from({length: 5}, (_, i) => ({id: `match-${i}`,
          finishedAt: '2026-11-18T10:30:00Z', defender: user, attacker: {nickname: `Past match ${i}`}}))],
        'GET /api/duels/fixture/state': current,
      };
      const key = `${request.method()} ${path}`;
      const account = request.headers().authorization;
      if (key === 'POST /api/duels') {
        const id = ['guest', 'guest-next', 'guest-last'][guestCreates++];
        assert.equal(account, undefined);
        assert.equal(request.headers()['x-seat-token'], ['guest', 'guest-next', 'guest-last']
          .slice(0, guestCreates - 1).at(-1)?.replace(/^/, 'fixture-'));
        return route.fulfill({status: 201, json: {id, seatToken: `fixture-${id}`}});
      }
      const guestRoom = path.match(/^\/api\/duels\/(guest(?:-\w+)?)\/state$/)?.[1];
      if (guestRoom) {
        if (account || request.headers()['x-seat-token'] !== `fixture-${guestRoom}`) errors.push('Wrong guest credentials');
        const done = finishedGuestRooms.has(guestRoom);
        return route.fulfill({json: {...board, matchId: guestRoom, joined: done, done, orders: []}});
      }
      if (key !== 'POST /api/auth/register' && account !== 'Bearer fixture-account') {
        errors.push(`Wrong account credentials for ${key}`);
      }
      if (key === 'GET /api/duels/fixture/state' && closureCode) {
        return route.fulfill({status: 404, json: {code: closureCode}});
      }
      if (key === 'GET /api/auth/me' && authMeFailure) {
        authMeFailure = false;
        return route.fulfill({status: 503, json: {status: 503}});
      }
      if (key === 'POST /api/auth/register' && ++registrationAttempts === 1) {
        return route.fulfill({ status: 409, json: { status: 409 } });
      }
      if (key === 'POST /api/duels/random/join') {
        assert.equal(request.postData(), null);
        searches++;
        return matchAvailable ? route.fulfill({json: {id: duel.id}}) : route.fulfill({status: 204});
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
        if (current.seat === 0) {
          if (move.wave !== 1 || move.action !== 'mine' || move.cell !== 9) errors.push(JSON.stringify(move));
          current = { ...current, trapCells: [], towers: [...current.towers, { type: 'mine', cell: 9, hp: 1, max: 1 }] };
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
      if (key === 'GET /api/duels' || key === 'GET /api/matches/mine') {
        const take = Number(url.searchParams.get('take'));
        const skip = Number(url.searchParams.get('skip'));
        assert.equal(take, 6);
        return route.fulfill({json: fixtures[key].slice(skip, skip + take)});
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
    const storage = () => page.evaluate(() => ({ account: localStorage.getItem('tower:token'),
      guest: sessionStorage.getItem('duel:guest'),
      preferences: Object.keys(localStorage).filter(key => key.startsWith('truck-kun-tower:')).sort() }));
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
    const help = button('Справка');
    await help.click({timeout: 4000});
    const guide = page.getByRole('dialog', {name: 'Справка'});
    const units = guide.getByRole('list', {name: 'Юниты'});
    const structures = guide.getByRole('list', {name: 'Постройки'});
    const ruleCard = (list, name) => list.getByRole('listitem').filter({has: page.getByRole('heading', {name, exact: true})});
    const swarmRule = ruleCard(units, 'Стайка');
    const armoredRule = ruleCard(units, 'Бронированные');
    const apcRule = ruleCard(units, 'Бронетранспортёр');
    await visible(swarmRule);
    assert.deepEqual(await swarmRule.locator('dd').allTextContents(), ['3–7', '0', '0', '1,75', '1 крепости / 1 забору']);
    assert.match(await armoredRule.innerText(), /2 забору и башне.*1 крепости.*не по диагонали.*продолжает путь/is);
    assert.deepEqual((await armoredRule.locator('dd').allTextContents()).slice(1, 4), ['1', '2', '2,5']);
    await visible(apcRule.getByText('Ломает забор и высаживает пассажиров'));
    assert.match(await ruleCard(structures, 'Руна').innerText(), /24.*2.*3.*1.*3 целям/is);
    const budgets = guide.getByRole('table', {name: 'Монеты по волнам'});
    await visible(budgets.getByRole('columnheader', {name: 'Бонус за постройку'}));
    assert.deepEqual(await budgets.locator('tbody tr').evaluateAll(rows =>
      rows.map(row => Array.from(row.cells, cell => cell.textContent))),
      [['1', '11', '16', '—'], ['2', '12', '17', '+1'], ['3', '14', '20', '+1'],
        ['4', '16', '24', '+2'], ['5', '18', '29', '—']]);
    await visible(guide.getByText(/Неистраченные монеты не переносятся/));
    await visible(guide.getByText(/Крепость не считается/));
    assert.ok(await guide.evaluate(node => node.getBoundingClientRect().width <= innerWidth && node.scrollWidth <= node.clientWidth));
    const statIcons = swarmRule.locator('dt svg');
    assert.deepEqual(await statIcons.locator('use').evaluateAll(nodes => nodes.map(node => node.getAttribute('href'))),
      ['heart', 'attack-radius', 'attack-interval', 'runner', 'hit-spark'].map(id => `actors.svg#${id}`));
    await page.waitForFunction(() => [...document.querySelectorAll('.guide-card dt svg')]
      .every(svg => svg.getBBox().width > 0 && svg.getAttribute('aria-hidden') === 'true'));
    const art = units.locator('.guide-portrait svg');
    assert.deepEqual(await art.locator('use').evaluateAll(nodes => nodes.map(node => node.getAttribute('href'))),
      ['swarm', 'armored', 'apc'].map(id => `actors.svg#${id}`));
    for (const guideWidth of [706, width]) {
      await page.setViewportSize({width: guideWidth, height: 900});
      const layout = await units.getByRole('listitem').evaluateAll(cards => ({
        columns: new Set(cards.map(card => card.getBoundingClientRect().left)).size,
        overflow: cards.flatMap(card => {
          const edge = card.getBoundingClientRect().right;
          return [...card.querySelectorAll('h4, dt, dd')].filter(node => {
            const text = document.createRange();
            text.selectNodeContents(node);
            return [...text.getClientRects()].some(line => line.right > edge + 1);
          }).map(node => node.textContent);
        }),
      }));
      assert.deepEqual(layout, {columns: guideWidth === 320 ? 1 : 2, overflow: []}, `Guide layout at ${guideWidth}px`);
      assert.ok(await guide.evaluate(node => node.scrollWidth <= node.clientWidth));
    }
    await shot('guide');
    await page.keyboard.press('Escape');
    assert.equal(await guide.isHidden(), true);
    assert.equal(await help.evaluate(node => node === document.activeElement), true);

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
    await button('Guide').click();
    const englishGuide = page.getByRole('dialog', {name: 'Guide'});
    const englishUnits = englishGuide.getByRole('list', {name: 'Units'});
    await visible(englishUnits.getByText('Ticks per tile', {exact: true}).first());
    await visible(englishUnits.getByText('1.75', {exact: true}));
    await visible(ruleCard(englishUnits, 'Armored slimes').getByText(/not diagonally.*continue along the route/i));
    await visible(englishGuide.getByText(/Fewer ticks per tile means faster movement/));
    const englishBudgets = englishGuide.getByRole('table', {name: 'Coins by wave'});
    await visible(englishBudgets.getByRole('row', {name: '4 16 24 +2'}));
    await visible(englishGuide.getByText(/Unspent coins do not carry over/));
    await page.keyboard.press('Escape');
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
    const languageButtons = [button('RU'), button('EN')];
    const topEdge = locator => locator.evaluate(node => {
      const box = node.getBoundingClientRect();
      const css = getComputedStyle(node);
      return { top: box.top, bottom: box.bottom, border: css.borderTop };
    });
    async function checkLanguageIndicator(locator) {
      const indicator = await locator.evaluate(node => {
        const css = getComputedStyle(node);
        return { outline: css.outlineStyle, shadow: css.boxShadow };
      });
      assert.equal(indicator.outline, 'none', 'No outline over the button edge');
      assert.match(indicator.shadow, /0px 0px 0px 1px inset$/, 'Use a thin line inside the border');
      assert.deepEqual(await topEdge(languageButtons[0]), await topEdge(languageButtons[1]),
        'Both language buttons must keep the same top edge');
    }
    async function checkLanguageFill(selected) {
      assert.equal(await selected.getAttribute('aria-pressed'), 'true');
      await selected.hover();
      const style = await selected.evaluate(node => {
        const probe = document.createElement('span');
        probe.style.backgroundColor = 'var(--ddlc-pink)';
        node.append(probe);
        const pink = getComputedStyle(probe).backgroundColor;
        probe.remove();
        const css = getComputedStyle(node);
        return { background: css.backgroundColor, pink, outline: css.outlineStyle, shadow: css.boxShadow };
      });
      assert.equal(style.background, style.pink, 'The selected language has a pink fill');
      assert.deepEqual([style.outline, style.shadow], ['none', 'none'], 'The selected language needs no hover ring');
    }
    async function checkLanguageSeam(inactive) {
      await inactive.hover();
      await checkLanguageIndicator(inactive);
      const seam = await page.locator('.language').evaluate(group => {
        const [left, right] = group.querySelectorAll('button');
        return {
          gap: right.getBoundingClientRect().left - left.getBoundingClientRect().right,
          separator: parseFloat(getComputedStyle(left).borderInlineEndWidth) +
            parseFloat(getComputedStyle(right).borderInlineStartWidth),
        };
      });
      assert.deepEqual(seam, { gap: 0, separator: 0 },
        'The hover outline meets the selected fill without a stripe');
    }
    await checkLanguageSeam(languageButtons[0]);
    await checkLanguageFill(languageButtons[1]);
    await button(/Theme: system/i).click();
    await languageButtons[0].click();
    await checkLanguageFill(languageButtons[0]);
    await checkLanguageSeam(languageButtons[1]);
    await languageButtons[1].click();
    await button(/Theme: light/i).click();
    await button(/Theme: dark/i).click();
    await page.mouse.move(0, 0);
    await button(/^Sound$/).focus();
    for (const language of languageButtons) {
      await page.keyboard.press('Tab');
      assert.equal(await language.evaluate(node => node.matches(':focus-visible')), true);
      await checkLanguageIndicator(language);
    }
    await page.emulateMedia({ forcedColors: 'active' });
    assert.equal(await languageButtons[1].evaluate(node => getComputedStyle(node).outlineStyle), 'solid',
      'Keyboard focus remains visible with forced colors');
    await page.emulateMedia({ forcedColors: 'none' });
    await shot('en-auth');
    await button('Register').click();
    await visible(page.getByRole('heading', { name: 'Lobby' }));
    await visible(page.getByText('November 20, 2026'));
    await visible(page.getByText('Online: 3'));
    const onlineRow = page.locator('#online-players li').filter({hasText: 'Огненный слизень'});
    await visible(onlineRow.getByRole('button', {name: 'Invite to a duel'}));
    await visible(onlineRow.getByRole('button', {name: 'Send friend request'}));
    const settled = page.locator('.duel').filter({hasText: 'Побеждённый'});
    assert.equal(await settled.getByRole('link', {name: 'Replay'}).getAttribute('href', {timeout: 4000}), '?replay=m-old');
    assert.equal(await settled.getByRole('button', {name: 'Continue'}).count(), 0);
    assert.equal(await page.locator('.duel').filter({hasText: 'Сбежавший'}).getByRole('link', {name: 'Replay'}).count(), 0);
    const history = page.getByRole('region', {name: 'Match history'});
    await visible(history.getByText(/November 19, 2026/));
    assert.equal(await history.getByRole('link', {name: 'Replay'}).getAttribute('href'), '?replay=m-old');
    const duels = page.getByRole('region', {name: 'My duels'});
    await visible(duels.getByRole('button', {name: 'Leave', exact: true}));
    assert.equal(await page.getByText(/Tabs share the account/).count(), 0);
    for (const list of [duels, history]) {
      await visible(list.getByText('Page 1', {exact: true}));
      assert.equal(await list.getByRole('listitem').count(), 5);
      assert.equal(await list.getByRole('button', {name: 'Previous'}).isDisabled(), true);
    }
    await visible(duels.getByText('Guest', {exact: true}));
    await visible(duels.getByText('open seat', {exact: true}));
    await duels.getByRole('button', {name: 'Next'}).click();
    await visible(duels.getByText('Past duel 3'));
    const restarted = duels.locator('.duel').filter({hasText: 'Past duel 3'});
    await visible(restarted.getByText('The server restarted, so the board was not kept. Start a new duel'));
    assert.equal(await restarted.getByRole('button', {name: 'New duel'}).count(), 1);
    assert.equal(await duels.getByRole('button', {name: 'New duel'}).count(), 1,
      'A finished duel asks for no new one');
    assert.equal(await duels.getByRole('listitem').count(), 2);
    assert.equal(await duels.getByRole('button', {name: 'Next'}).isDisabled(), true);
    await visible(history.getByText('Page 1', {exact: true}));
    await history.getByRole('button', {name: 'Next'}).click();
    await visible(history.getByText('Past match 4'));
    assert.equal(await history.getByRole('listitem').count(), 1);
    assert.equal(await history.getByRole('button', {name: 'Next'}).isDisabled(), true);
    await history.getByRole('button', {name: 'Previous'}).click();
    await visible(history.getByText('Page 1', {exact: true}));
    await visible(duels.getByText('Page 2', {exact: true}));
    await duels.getByRole('button', {name: 'Previous'}).click();
    await visible(duels.getByText('Page 1', {exact: true}));
    assert.equal(await page.getByRole('checkbox', {name: 'Random opponent'}).isChecked(), true);
    const search = page.locator('#random-join');
    const searchRequest = page.waitForResponse(res => res.url().endsWith('/api/duels/random/join'));
    await search.click();
    assert.equal((await searchRequest).status(), 204);
    await visible(button('Cancel search'));
    assert.equal(await page.locator('header').evaluate(node => node.inert), true);
    const continuing = button('Continue');
    const blocked = await continuing.boundingBox();
    assert.ok(blocked);
    await page.mouse.click(blocked.x + blocked.width / 2, blocked.y + blocked.height / 2);
    assert.equal(await page.locator('#game').isHidden(), true, 'A click must not enter another duel');
    assert.equal(await continuing.evaluate(node => { node.focus(); return node === document.activeElement; }), false);
    await page.clock.runFor(1250);
    await visible(page.getByText(/Wait time: 00:0[12]/));
    await search.click();
    await visible(button('Join a random duel'));
    assert.equal(await page.locator('header').evaluate(node => node.inert), false);
    const totalSearches = searches;
    await page.clock.runFor(2200);
    assert.equal(searches, totalSearches, 'Cancel stops future requests');
    await visible(button('Continue'));
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

    const emptySearch = page.waitForResponse(res => res.url().endsWith('/api/duels/random/join') && res.status() === 204);
    await search.click();
    await emptySearch;
    matchAvailable = true;
    await page.clock.runFor(2500);
    await visible(page.getByRole('heading', { name: /Wave 1 of 5.*planning/ }));
    assert.equal(await page.locator('header').evaluate(node => node.inert), false);
    await visible(page.getByText(`Opponent: ${opponent.nickname}`, { exact: true }));
    await visible(page.getByRole('button', {name: /^leave$/i}).and(page.locator('#leave')));
    const [readyBox, clockBox, boardBox] = await Promise.all(['ready', 'clock', 'board'].map(id => page.locator(`#${id}`).boundingBox()));
    assert.ok(readyBox && clockBox && boardBox && readyBox.y + readyBox.height <= boardBox.y &&
      readyBox.y < clockBox.y + clockBox.height && clockBox.y < readyBox.y + readyBox.height,
    'The ready button belongs beside the clock above the board');
    const corner = page.locator('#board-corner');
    assert.ok(await corner.count(), 'The coordinate corner contains a sticker');
    assert.ok(await corner.evaluate(node => {
      const bounds = node.getBoundingClientRect();
      const rows = document.querySelector('#board-rows').getBoundingClientRect();
      const columns = document.querySelector('#board-columns').getBoundingClientRect();
      return Math.abs(bounds.left - rows.left) < 1 && Math.abs(bounds.top - columns.top) < 1 &&
        node.getAttribute('aria-hidden') === 'true' && node.querySelector('use').getBBox().width > 0;
    }), 'The decorative sprite renders in the corner, not over an axis');
    const row = button('Row 2');
    const column = button('Column 3');
    assert.equal(await page.getByRole('button', {name: /^Row \d+$/}).count(), board.height);
    assert.equal(await page.getByRole('button', {name: /^Column \d+$/}).count(), board.width);
    const axis = page.locator('#map-axis rect');
    await row.hover();
    assert.deepEqual(await axis.evaluate(node => [node.x.baseVal.value, node.y.baseVal.value, node.width.baseVal.value, node.height.baseVal.value]), [0, 40, 200, 40]);
    await column.hover();
    assert.deepEqual(await axis.evaluate(node => [node.x.baseVal.value, node.y.baseVal.value, node.width.baseVal.value, node.height.baseVal.value]), [80, 0, 40, 120]);
    await page.mouse.move(0, 0);
    assert.equal(await axis.count(), 0);
    assert.equal(await page.locator('#hint').isHidden(), true, 'No hint before group selection');
    assert.equal(await button('Gate 1').isDisabled(), true, 'Select a group before deploying');
    await button(/Smart slimes/).click();
    await button('Gate 1').hover();
    await visible(page.getByText(/Gate 1.*shortest route/));
    assert.equal(moves, 0, 'A route preview must not submit a move');
    assert.equal(await page.locator('#invite').isHidden(), true);
    await button('RU').click();
    await visible(page.getByRole('heading', { name: /Волна 1 из 5.*подготовка/ }));
    await button('Вход 1').hover();
    await visible(page.getByText(/Вход 1.*кратчайший путь/));
    await visible(page.locator('#actions [data-action=armored] .action-hint').filter({hasText: /останавливаются у башен/i}));
    const giantOrder = page.locator('#orders li').filter({hasText: /Гигант.*Вход 1/});
    await visible(giantOrder);
    await giantOrder.getByRole('button').hover();
    const undoMark = page.locator('#map-undo rect');
    assert.equal(await undoMark.getAttribute('x', {timeout: 1000}), '120');
    const undoSprite = page.locator('#map-undo use');
    assert.equal(await undoSprite.getAttribute('href', {timeout: 1000}), 'actors.svg#giant');
    await page.mouse.move(0, 0);
    assert.equal(await undoMark.count(), 0);
    assert.equal(await undoSprite.count(), 0);
    assert.equal(await button(/Умные слизни/).getAttribute('aria-pressed'), 'true');
    assert.equal(await page.evaluate(() => new URL(location.href).searchParams.get('room')), 'fixture');
    await shot('ru-game');
    await button('Вход 1').click();
    const gameError = page.getByRole('alert').filter({ hasText: /Этот ход недоступен/ });
    await visible(gameError.getByText('Ошибка', { exact: true }));
    assert.deepEqual(await alertStyle(gameError), { position: 'sticky', animation: 'none', masked: true });
    await button('EN').click();
    await visible(gameError.getByText('Error', { exact: true }));
    assert.equal(moves, 1);
    await button('Gate 1').click();
    const limitError = page.getByRole('alert').filter({ hasText: /Purchase limit/ });
    await visible(limitError);
    assert.doesNotMatch(await limitError.textContent(), /Достигнут/);
    await button('RU').click();
    await visible(page.getByRole('alert').filter({ hasText: /Достигнут лимит покупок этого типа/ }));
    await button('EN').click();
    await page.clock.runFor(3000);
    await visible(limitError);
    if (width === 1280) await page.emulateMedia({reducedMotion: 'no-preference'});
    await page.clock.runFor(2100);
    if (width === 1280) {
      assert.ok(await limitError.evaluate(node => {
        const fade = node.getAnimations().find(animation => animation.effect.getKeyframes().at(-1).opacity === '0');
        if (!fade) return false;
        fade.pause();
        fade.currentTime = fade.effect.getTiming().duration / 2;
        const opacity = Number(getComputedStyle(node).opacity);
        fade.finish();
        return opacity > 0 && opacity < 1;
      }), 'The error stays on screen and fades before it is removed');
    } else {
      assert.equal(await limitError.isHidden(), true, 'Reduced motion skips the fade');
    }
    await limitError.waitFor({state: 'hidden'});
    await button('Gate 1').press('Enter');
    const smartOrder = page.locator('#orders li').filter({hasText: /Smart slimes · Gate 1/});
    await smartOrder.getByRole('button').hover();
    assert.equal(await undoSprite.getAttribute('href'), 'actors.svg#smart');
    await page.locator('#orders li').filter({hasText: /Giant · Gate 1/}).getByRole('button').hover();
    assert.equal(await undoSprite.getAttribute('href'), 'actors.svg#giant');
    assert.equal(await undoMark.getAttribute('x'), '120');
    await page.mouse.move(0, 0);
    assert.equal(await page.locator('#commit').count(), 0, 'There must be no second confirmation button');
    assert.equal(moves, 3);
    await gameError.waitFor({ state: 'hidden' });
    assert.equal((await storage()).account, 'fixture-account');
    await shot('en-game');

    const card = type => page.locator(`#actions [data-action=${type}]`);
    assert.match(await card('armored').innerText(), /stop to attack adjacent towers/i);
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
    await visible(page.locator('#clock'));
    const timeBefore = await page.locator('#clock').textContent();
    await page.clock.runFor(1250);
    assert.notEqual(await page.locator('#clock').textContent(), timeBefore);

    await pushState({ seat: 0, locked: false, fenceCells: [4, 8, 9],
      prices: { bolt: 3, pulse: 4, fence: 3, spikes: 2, mine: 2 }, limits: { bolt: 1, pulse: 2, fence: 1 },
      towers: [{ type: 'bolt', cell: 2, hp: 5, max: 5 }], fences: [{ cell: 8, hp: 5, max: 5 }] });
    for (const type of ['bolt', 'fence']) {
      assert.match(await card(type).innerText(), /1\/1/);
      assert.equal(await card(type).isDisabled(), true);
    }
    const limit = card('pulse').locator('.action-limit');
    assert.equal(await limit.innerText(), '0/2');
    assert.equal(await limit.locator('use').getAttribute('href', {timeout: 2000}), 'actors.svg#groups-left');
    for (const type of ['mine', 'spikes']) {
      assert.match(await card(type).innerText(), /One per route/);
      assert.equal(await card(type).isDisabled(), true, 'Missing trapCells must fail closed');
    }
    await pushState({ trapCells: [8, 9] });
    assert.equal(await card('mine').isEnabled(), true);
    await card('mine').click();
    assert.deepEqual(await page.locator('#cells button:visible').evaluateAll(nodes => nodes.map(n => n.dataset.cell)), ['9']);
    await page.locator('#cells [data-cell="9"]').click();
    await visible(page.locator('#orders li').filter({ hasText: /Mine/ }));
    assert.equal(await page.locator('#cells [data-cell="9"]').isHidden(), true);
    await button('RU').click();
    assert.match(await card('mine').innerText(), /Одна ловушка на маршрут/);
    await shot('ru-defender');
    await button('EN').click();
    assert.equal(await card('mine').isDisabled(), true);

    for (const [code, english, russian] of [
      ['room_abandoned', /disconnected or did not act/, /отключился или не сделал ход/],
      ['room_cancelled', /your opponent left/i, /соперник вышел/],
    ]) {
      closureCode = code;
      await page.evaluate(() => sync());
      await visible(page.getByRole('alert').filter({hasText: english}));
      await button('RU').click();
      await visible(page.getByRole('alert').filter({hasText: russian}));
      await button('EN').click();
      closureCode = null;
      await button('Continue').click();
      await visible(page.locator('#actions [data-action=pulse]'));
    }

    const linked = await context.newPage();
    linked.on('pageerror', error => errors.push(error.message));
    authMeFailure = true;
    await linked.goto(`${origin}/?room=fixture`);
    await linked.getByRole('button', {name: 'Retry session check'}).click({timeout: 4000});
    await visible(linked.locator('#game'));
    assert.equal(await linked.locator('#auth').isHidden(), true);
    const accountLobby = await context.newPage();
    await accountLobby.goto(origin);
    await accountLobby.getByRole('button', {name: 'Sign out', exact: true}).click();
    await visible(linked.getByRole('button', {name: 'Join the duel', exact: true}));
    await visible(page.getByRole('button', {name: 'Join the duel', exact: true}));
    await linked.close();
    await accountLobby.close();

    const fresh = await context.newPage();
    fresh.on('pageerror', error => errors.push(error.message));
    await fresh.goto(origin);
    await visible(fresh.getByRole('button', { name: 'Play as guest' }));
    assert.equal(await fresh.evaluate(() => sessionStorage.length), 0);
    await fresh.getByRole('button', { name: 'Play as guest' }).click();
    await visible(fresh.getByRole('heading', { name: 'Waiting for the second player' }));
    assert.equal(await fresh.locator('#ready').isHidden(), true);
    assert.equal(await fresh.getByLabel('Link for the second player').inputValue(), `${origin}/?room=guest`);
    await fresh.getByRole('button', {name: 'Copy link', exact: true}).click();
    await visible(fresh.getByRole('button', {name: 'Link copied', exact: true}));
    await fresh.getByRole('button', { name: 'RU', exact: true }).click();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    const guest = await fresh.evaluate(() => sessionStorage.getItem('duel:guest'));
    assert.equal(guest, 'fixture-guest');
    await fresh.reload();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    finishedGuestRooms.add('guest');
    await fresh.evaluate(() => refresh());
    await fresh.getByRole('button', { name: 'Новая дуэль', exact: true }).click();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    assert.equal(await fresh.locator('#auth').isHidden(), true);
    assert.equal(await fresh.locator('#link').inputValue(), `${origin}/?room=guest-next`);
    await fresh.reload();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    assert.equal(new URL(fresh.url()).searchParams.get('room'), 'guest-next');

    finishedGuestRooms.add('guest-next');
    await fresh.evaluate(() => refresh());
    await fresh.locator('#leave').click();
    await fresh.getByRole('button', { name: 'Играть гостем', exact: true }).click();
    await visible(fresh.getByRole('heading', { name: 'Ждём второго игрока' }));
    assert.equal(await fresh.locator('#link').inputValue(), `${origin}/?room=guest-last`);
    const stored = await storage();
    assert.equal(stored.guest, null);
    assert.ok(!stored.account, 'Signing out clears the shared account');
    assert.deepEqual(stored.preferences, ['truck-kun-tower:lang', 'truck-kun-tower:sound', 'truck-kun-tower:theme']);
    assert.deepEqual(errors, []);
    await context.close();
    console.log(`${width}px: preferences, auth, lobby, purchase limits, traps, skip, and guest isolation passed`);
  }
} finally {
  await browser?.close();
  server.close();
}
