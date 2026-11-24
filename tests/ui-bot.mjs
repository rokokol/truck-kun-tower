import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

// A solo board boots the same six classic scripts as a live duel, so a top-level name clash would stop a script here too
// This fixture stubs the API and the socket, not the scripts; assertions read the screen the user sees
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const root = fileURLToPath(new URL('../wwwroot/', import.meta.url));
const mime = new Map([['.html', 'text/html'], ['.js', 'text/javascript'], ['.css', 'text/css'],
  ['.svg', 'image/svg+xml'], ['.woff2', 'font/woff2'], ['.json', 'application/json'],
  ['.webmanifest', 'application/manifest+json'], ['.ico', 'image/x-icon'], ['.png', 'image/png']]);
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || 'chromium',
  args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage',
    '--autoplay-policy=document-user-activation-required'] });

// The same minimal 3x3 board the replay fixture uses: one road, one gate, one keep
const soloState = {
  randomOpponent: false,
  joined: true,
  seat: 0,
  side: 'defender',
  last: 0,
  done: false,
  locked: false,
  gold: 11,
  hp: 10,
  maxHp: 10,
  matchId: 'solo-match',
  deadline: null,
  secondsLeft: 60,
  replayMs: 0,
  replayLeftMs: 0,
  swap: null,
  opponent: null,
  opponentId: null,
  bot: true,
  botOpponent: true,
  width: 3,
  height: 3,
  paths: [[0, 1, 4]],
  alternatePaths: [],
  fenceCells: [1],
  trapCells: [],
  fences: [],
  towers: [],
  orders: [],
  refundableCells: [],
  prices: {bolt: 5, pulse: 6, cannon: 8, frost: 6, spikes: 3, mine: 3, fence: 4,
    swarm: 4, giant: 7, smart: 5, runner: 3, armored: 8, ranged: 14, apc: 13},
  limits: {bolt: 4, pulse: 3, cannon: 2, frost: 2, fence: 3, swarm: 4, giant: 2,
    smart: 2, runner: 3, armored: 2, ranged: 1, apc: 1},
  attackRows: {bolt: [0, 0, 1, 0, 0], cannon: [0, 1, 1, 1, 0],
    pulse: [0, 1, 1, 1, 0], frost: [0, 0, 1, 0, 0], ranged: [0, 0, 1, 0, 0]},
  frames: [],
  replayStart: null,
  waveHistory: [],
};

function board(id, side, practice, finished) {
  const seat = side === 'attacker' ? 1 : 0;
  return {...soloState, side, seat, joined: practice, bot: practice, botOpponent: practice,
    matchId: `${id}-match`, done: Boolean(finished), last: finished ? 5 : 0,
    canSwap: practice && !finished, secondsLeft: practice || finished ? 0 : 60, frames: [], replayStart: null,
    waveHistory: finished ? [{wave: 5, attack: [{type: 'runner', cell: 0, count: 1}], added: [], removed: [], lost: []}] : []};
}

function fixture(options = {}) {
  const rooms = new Map();
  let serial = 0;
  async function serve(route) {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const headers = request.headers();
    if (path === '/api/duels' && request.method() === 'POST') {
      if (options.requireSeat && !headers['x-seat-token'])
        return route.fulfill({status: 409, json: {detail: 'Сначала выйди из текущей дуэли', code: 'duel_busy'}});
      const body = JSON.parse(request.postData() || '{}');
      const practice = body.practice === true;
      const side = body.side ?? 'defender';
      const id = `solo-${++serial}`;
      const room = {id, practice, seatToken: `${id}-seat`,
        state: board(id, side, practice, options.finished && serial === 1)};
      if (options.archived && room.state.done) room.state.practiceReplay = true;
      rooms.set(id, room);
      return route.fulfill({status: 201, json: {
        id, side, seatToken: room.seatToken,
        room: {id, mySide: side, opponent: null, status: practice ? 'Playing' : 'Waiting',
          createdAt: new Date().toISOString()},
      }});
    }
    if (path === '/api/duels' && request.method() === 'GET') {
      return route.fulfill({json: [...rooms.values()].filter(room => room.practice && !room.state.done)
        .map(room => ({id: room.id, practice: true, mySide: room.state.side, status: 'Playing'}))});
    }
    const swapMatch = path.match(/^\/api\/duels\/([^/]+)\/swap$/);
    if (swapMatch && request.method() === 'POST') {
      const room = rooms.get(swapMatch[1]);
      room.state = board(room.id, room.state.side === 'attacker' ? 'defender' : 'attacker', true, false);
      return route.fulfill({json: room.state});
    }
    const closeMatch = path.match(/^\/api\/duels\/([^/]+)$/);
    if (closeMatch && request.method() === 'DELETE') {
      rooms.delete(closeMatch[1]);
      return route.fulfill({status: 204});
    }
    const stateMatch = path.match(/^\/api\/duels\/([^/]+)\/state$/);
    if (stateMatch) {
      const id = decodeURIComponent(stateMatch[1]);
      if (options.requireSeat && id === 'held') return route.fulfill({json: board(id, 'defender', false, true)});
      const room = rooms.get(id);
      if (!room) return route.fulfill({status: 404, json: {status: 404}});
      return route.fulfill({json: room.state});
    }
    const moveMatch = path.match(/^\/api\/duels\/([^/]+)\/moves$/);
    if (moveMatch && request.method() === 'POST') {
      const room = rooms.get(decodeURIComponent(moveMatch[1]));
      if (!room) return route.fulfill({status: 404, json: {status: 404}});
      const body = JSON.parse(request.postData() || '{}');
      if (body.action === 'rematch' && room.practice) {
        const matchId = `${room.state.matchId}-rematch`;
        room.state = {...board(room.id, room.state.side, true, false), matchId};
      }
      return route.fulfill({json: room.state});
    }
    if (path === '/api/matches/mine' && options.archived) {
      return route.fulfill({json: [{id: 'solo-1-match', practice: true, hasReplay: true, winner: 'Defender',
        attacker: {id: 'me'}, finishedAt: '2024-01-02T00:00:00Z'}]});
    }
    if (path === '/api/auth/me') return route.fulfill({json: {id: 'me', nickname: 'Solo', createdAt: '2024-01-02T00:00:00Z'}});
    if (path === '/api/users/online') return route.fulfill({json: {count: 0, players: []}});
    if (path.startsWith('/api/')) return route.fulfill({json: []});
    if (path === '/bell/negotiate') return route.fulfill({json: {
      connectionId: 'fixture', availableTransports: [{transport: 'WebSockets', transferFormats: ['Text']}],
    }});
    if (path.startsWith('/bell')) return route.fulfill({status: 503, body: ''});
    const file = path === '/' ? '/index.html' : path;
    try {
      const body = await readFile(root + file.slice(1));
      const type = mime.get(file.slice(file.lastIndexOf('.'))) ?? 'application/octet-stream';
      return route.fulfill({contentType: type, body});
    } catch {
      return route.fulfill({status: 404, body: ''});
    }
  }
  return serve;
}

async function wire(page, options = {}) {
  const errors = [];
  const outside = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('requestfailed', request => {
    const reason = request.failure()?.errorText ?? '';
    if (!reason.includes('ERR_ABORTED')) errors.push(`${request.method()} ${request.url()}: ${reason}`);
  });
  page.on('request', request => {
    const url = request.url();
    if (/^https?:/.test(url) && new URL(url).hostname !== 'boot.test') outside.push(url);
  });
  await page.route('http://boot.test/**', fixture(options));
  await page.routeWebSocket(/\/bell\?/, ws => {
    ws.onMessage(data => {
      for (const frame of String(data).split('\x1e')) {
        if (!frame) continue;
        if (frame.includes('"protocol":"json"')) { ws.send('{}\x1e'); continue; }
        try {
          const message = JSON.parse(frame);
          if (message.type === 1 && message.invocationId)
            ws.send(JSON.stringify({type: 3, invocationId: message.invocationId, result: null}) + '\x1e');
        } catch {}
      }
    });
  });
  return {errors, outside};
}

async function bootProbe(page) {
  await page.waitForFunction(() => typeof tile === 'number' && typeof i18n === 'object' &&
    typeof sounds === 'object' && typeof Tower === 'object' &&
    typeof window.signalR?.HubConnectionBuilder === 'function' &&
    typeof document.getElementById('open-guide').onclick === 'function', undefined, {timeout: 8000});
}

async function waitForOpponent(page) {
  await page.locator('#cells button').first().waitFor({state: 'attached', timeout: 8000});
  await page.waitForFunction(() => document.getElementById('rival').textContent.trim().length > 0,
    undefined, {timeout: 8000});
}

function assertClean(check, label) {
  assert.deepEqual(check.errors, [], `${label}: the real script set runs without an error`);
  assert.deepEqual(check.outside, [], `${label}: no request leaves the fixture`);
}

try {
  const account = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  await account.addInitScript(() => localStorage.setItem('tower:token', 'fixture-token'));
  const accountCheck = await wire(account);
  await account.goto('http://boot.test/');
  await bootProbe(account);
  const playBot = account.locator('#play-bot');
  await playBot.waitFor({timeout: 8000});
  assert.equal(await playBot.isVisible(), true, 'the lobby shows the practice button');
  assert.equal((await playBot.textContent()).trim(), 'Играть с ботом');
  assert.equal(await account.locator('#lobby').getByText('Нейробот займёт второе место').count(), 0, 'the lobby keeps the practice button free of a caption');
  await account.locator('input[name="side"][value="attacker"]').check();
  await account.waitForFunction(() => document.querySelector('input[name="side"][value="attacker"]').checked === true);
  await playBot.click();
  await waitForOpponent(account);
  assert.equal((await account.locator('#rival').textContent()).trim(), 'Соперник: бот', 'the opponent panel names a bot');
  assert.equal((await account.locator('#role').textContent()).trim(), 'Атакующий', 'the chosen attacker side stays on the board');
  assert.equal((await account.locator('#actions button strong').first().textContent()).trim(), 'Стайка', 'the attacker sees a real attacker action');
  assert.equal(await account.locator('#invite').isHidden(), true, 'the practice board hides the invite link');
  assert.equal(await account.locator('#swap').isVisible(), true, 'the practice board offers a seat swap');
  assert.equal(await account.locator('#clock').isHidden(), true, 'practice has no move clock');
  assert.equal(await account.locator('#swapAsk').isHidden(), true, 'the practice board hides the swap request');
  assert.equal(await account.locator('#swapWait').isHidden(), true, 'the practice board hides the swap wait note');
  assert.equal(await account.locator('#watchReplay').isHidden(), true, 'the practice board hides the replay link');
  assert.ok(await account.locator('#cells button').count() > 0, 'the board draws cells');
  assert.ok((await account.locator('#map-road').innerHTML()).includes('road-tile'), 'the board draws the road');
  await account.locator('#swap').click();
  await account.waitForFunction(() => document.querySelector('#role').textContent === 'Защитник');
  await account.locator('#practice-back').click();
  const practice = account.locator('#lobby li.duel').filter({hasText: 'Бот'});
  await practice.waitFor();
  assert.equal(await practice.locator('button.ghost').first().isHidden(), true, 'practice has no invite link');
  await practice.getByRole('button', {name: 'Продолжить', exact: true}).click();
  await waitForOpponent(account);
  assert.equal((await account.locator('#role').textContent()).trim(), 'Защитник');
  await account.locator('#practice-back').click();
  account.once('dialog', dialog => dialog.accept());
  await practice.getByRole('button', {name: 'Выйти', exact: true}).click();
  await practice.waitFor({state: 'detached'});
  assertClean(accountCheck, 'account lobby');
  console.log('PASS: the account lobby starts a bot duel and keeps the chosen side');
  await account.close();

  const rematch = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  await rematch.addInitScript(() => localStorage.setItem('tower:token', 'fixture-token'));
  const rematchCheck = await wire(rematch, {finished: true});
  await rematch.goto('http://boot.test/');
  await bootProbe(rematch);
  await rematch.locator('input[name="side"][value="attacker"]').check();
  await rematch.locator('#play-bot').click();
  await rematch.locator('#afterMatch').waitFor({state: 'visible', timeout: 8000});
  assert.equal(await rematch.locator('#watchReplay').isHidden(), true, 'a finished practice board hides the replay link');
  assert.equal(await rematch.locator('#wave-history').isHidden(), true, 'practice hides the completed wave history');
  await rematch.locator('#rematch').click();
  await rematch.locator('#planning').waitFor({state: 'visible', timeout: 8000});
  assert.equal((await rematch.locator('#rival').textContent()).trim(), 'Соперник: бот', 'a server rematch keeps the bot opponent');
  assert.equal((await rematch.locator('#role').textContent()).trim(), 'Атакующий', 'a server rematch keeps the chosen side');
  assert.equal(await rematch.locator('#afterMatch').isHidden(), true, 'a server rematch returns to planning');
  assert.equal(await rematch.locator('#invite').isHidden(), true, 'a server rematch keeps the invite link hidden');
  assert.equal(await rematch.locator('#swapAsk').isHidden(), true, 'a server rematch keeps the swap request hidden');
  assertClean(rematchCheck, 'server rematch');
  console.log('PASS: a server rematch keeps the bot and the chosen side');
  await rematch.close();

  const archived = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  await archived.addInitScript(() => localStorage.setItem('tower:token', 'fixture-token'));
  const archivedCheck = await wire(archived, {finished: true, archived: true});
  await archived.goto('http://boot.test/');
  await bootProbe(archived);
  await archived.locator('input[name="side"][value="attacker"]').check();
  await archived.locator('#play-bot').click();
  await archived.locator('#watchReplay').waitFor();
  await archived.locator('#leave').click();
  const history = archived.locator('[aria-labelledby="history-title"]').first();
  await history.getByText('Против бота', {exact: true}).waitFor();
  assert.equal(await history.getByText('Поражение', {exact: true}).isVisible(), true);
  assert.equal(await history.locator('a.replay-link').getAttribute('href'), '?replay=solo-1-match');
  assertClean(archivedCheck, 'practice history');
  await archived.close();

  const guest = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  const guestCheck = await wire(guest);
  await guest.goto('http://boot.test/');
  await bootProbe(guest);
  const startBot = guest.locator('#start-bot');
  await startBot.waitFor({timeout: 8000});
  assert.equal(await startBot.isVisible(), true, 'the guest screen shows the practice button');
  assert.equal((await startBot.textContent()).trim(), 'Играть с ботом');
  assert.equal(await guest.locator('#auth').getByText('Нейробот займёт второе место').count(), 0, 'the guest screen keeps the practice button free of a caption');
  assert.equal(await guest.locator('#auth input[type="radio"]').count(), 0, 'the guest screen keeps the invite seat rule');
  await startBot.click();
  await waitForOpponent(guest);
  assert.equal((await guest.locator('#rival').textContent()).trim(), 'Соперник: бот', 'the guest board names a bot');
  assert.equal((await guest.locator('#role').textContent()).trim(), 'Защитник', 'the guest keeps the defender chair');
  assert.equal(await guest.locator('#invite').isHidden(), true, 'the guest board hides the invite link');
  assert.equal(await guest.locator('#swap').isVisible(), true, 'the guest can swap sides');
  assert.equal(await guest.locator('#swapAsk').isHidden(), true, 'the guest board hides the swap request');
  assert.equal((await guest.locator('#actions button strong').first().textContent()).trim(), 'Арбалет', 'the defender sees a real defender action');
  assert.ok((await guest.locator('#map-road').innerHTML()).includes('road-tile'), 'the guest board draws the road');
  assert.equal(new URL(guest.url()).searchParams.get('room'), 'solo-1', 'the board keeps the room in the URL');
  await guest.reload();
  await bootProbe(guest);
  await waitForOpponent(guest);
  assert.equal((await guest.locator('#rival').textContent()).trim(), 'Соперник: бот', 'a guest reload keeps the bot opponent');
  assert.equal((await guest.locator('#role').textContent()).trim(), 'Защитник', 'a guest reload keeps the defender chair');
  assert.equal(await guest.locator('#invite').isHidden(), true, 'a guest reload keeps the invite link hidden');
  assert.equal(await guest.locator('#swap').isVisible(), true, 'the guest can swap sides after reload');
  await guest.locator('#practice-back').click();
  const guestEntry = guest.locator('#start');
  const botEntry = guest.locator('#start-bot');
  await guestEntry.waitFor();
  await botEntry.waitFor();
  assert.equal((await guestEntry.boundingBox()).width, (await botEntry.boundingBox()).width);
  await botEntry.click();
  await waitForOpponent(guest);
  assert.equal(new URL(guest.url()).searchParams.get('room'), 'solo-1');
  assert.equal(await guest.locator('#swapAsk').isHidden(), true, 'a guest reload keeps the swap request hidden');
  assert.equal(await guest.locator('#auth').isHidden(), true, 'a guest reload opens the board');
  assertClean(guestCheck, 'guest reload');
  console.log('PASS: a guest starts a bot duel and reloads the same board');
  await guest.close();

  const busy = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  await busy.addInitScript(() => {
    sessionStorage.setItem('tower:guest-room', 'held');
    sessionStorage.setItem('duel:held', 'held-seat');
  });
  const busyCheck = await wire(busy, {requireSeat: true});
  await busy.goto('http://boot.test/');
  await bootProbe(busy);
  await busy.locator('#start-bot').click();
  await waitForOpponent(busy);
  assert.equal((await busy.locator('#rival').textContent()).trim(), 'Соперник: бот', 'the held seat still opens a bot board');
  assert.equal(await busy.locator('#authError').isHidden(), true, 'the held seat token satisfies the one-duel rule');
  assertClean(busyCheck, 'guest seat');
  console.log('PASS: a guest sends the held seat token and can start one practice board');
  await busy.close();

  const again = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'ru-RU'});
  const againCheck = await wire(again, {finished: true});
  await again.goto('http://boot.test/');
  await bootProbe(again);
  await again.locator('#start-bot').click();
  await again.locator('#afterMatch').waitFor({state: 'visible', timeout: 8000});
  const beforeAgain = new URL(again.url()).searchParams.get('room');
  await again.locator('#again').click();
  await again.waitForFunction(previous => new URLSearchParams(location.search).get('room') !== previous,
    beforeAgain, {timeout: 8000});
  assert.notEqual(new URL(again.url()).searchParams.get('room'), beforeAgain, 'a guest new duel opens a new room');
  await waitForOpponent(again);
  assert.equal((await again.locator('#rival').textContent()).trim(), 'Соперник: бот', 'a guest new duel keeps the bot opponent');
  assert.equal(await again.locator('#planning').isVisible(), true, 'a guest new duel returns to planning');
  assert.equal((await again.locator('#role').textContent()).trim(), 'Защитник', 'a guest new duel keeps the defender chair');
  assert.equal(await again.locator('#invite').isHidden(), true, 'a guest new duel keeps the invite link hidden');
  assert.equal(await again.locator('#swapAsk').isHidden(), true, 'a guest new duel keeps the swap request hidden');
  assertClean(againCheck, 'guest new duel');
  console.log('PASS: the guest new-duel button keeps a bot board');
  await again.close();

  const english = await browser.newPage({viewport: {width: 900, height: 900}, locale: 'en-GB'});
  await english.addInitScript(() => localStorage.setItem('tower:token', 'fixture-token'));
  const englishCheck = await wire(english);
  await english.goto('http://boot.test/');
  await bootProbe(english);
  assert.equal((await english.locator('#play-bot').textContent()).trim(), 'Play a bot');
  assert.equal(await english.locator('#lobby').getByText('A neural bot takes the other chair').count(), 0, 'the English entry carries no caption');
  await english.locator('input[name="side"][value="attacker"]').check();
  await english.locator('#play-bot').click();
  await waitForOpponent(english);
  assert.equal((await english.locator('#rival').textContent()).trim(), 'Opponent: bot', 'the English opponent panel names a bot');
  assert.equal((await english.locator('#role').textContent()).trim(), 'Attacker');
  assert.equal((await english.locator('#actions button strong').first().textContent()).trim(), 'Swarm');
  assertClean(englishCheck, 'English strings');
  console.log('PASS: English strings name the bot entry and opponent');
  await english.close();
} finally {
  await browser.close();
}
