// The three screenshots the README shows are repository artifacts, so their recipe lives here
// rather than in a scratch directory: one command plays a real duel on a running game and writes
// docs/cover.png, docs/planning.png and docs/battle.png, or fails loudly with nothing half-written
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {existsSync} from 'node:fs';
import {rename, rm} from 'node:fs/promises';
import {join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const repo = fileURLToPath(new URL('..', import.meta.url));
const docs = join(repo, 'docs');

const USAGE = `node tools/shots.mjs [options]

  --url <url>     shoot a game other than https://tower.rokokol.art:8443
  --lang <code>   ru or en, the language the screenshots are taken in (default en)
  --out <dir>     write the screenshots somewhere other than docs/
  -h, --help      this text`;

const SHOTS = ['cover.png', 'planning.png', 'battle.png'];
const LANGUAGES = ['en', 'ru'];

function fail(message) {
  console.error(`shots: ${message}`);
  process.exit(1);
}

function parse(argv) {
  const options = {url: 'https://tower.rokokol.art:8443', lang: 'en', out: docs};
  for (let index = 0; index < argv.length; index++) {
    const flag = argv[index];
    if (flag === '-h' || flag === '--help') {
      console.log(USAGE);
      process.exit(0);
    }
    const value = argv[index + 1];
    if (!['--url', '--lang', '--out'].includes(flag)) fail(`unknown option ${flag}\n\n${USAGE}`);
    if (value === undefined || value.startsWith('--')) fail(`${flag} needs a value`);
    if (flag === '--lang') {
      if (!LANGUAGES.includes(value)) fail(`--lang takes ${LANGUAGES.join(' or ')}, not ${value}`);
      options.lang = value;
    } else if (flag === '--url') {
      options.url = value.replace(/\/$/, '');
    } else {
      options.out = resolve(value);
    }
    index++;
  }
  return options;
}

// playwright-core is not a dependency of the repository: the browser plumbing is borrowed from
// whatever install the machine has, the same way the browser tests borrow it
function playwright() {
  const from = process.env.PLAYWRIGHT_CORE || 'playwright-core';
  try {
    return createRequire(import.meta.url)(from);
  } catch {
    return fail(`cannot load ${from}; set PLAYWRIGHT_CORE to a playwright-core install`);
  }
}

function onPath(name) {
  if (name.includes('/')) {
    if (!existsSync(name)) fail(`${name} does not exist; set CHROMIUM to a browser binary`);
    return name;
  }
  for (const dir of (process.env.PATH || '').split(':')) {
    const candidate = join(dir, name);
    if (existsSync(candidate)) return candidate;
  }
  fail(`cannot find ${name} on PATH; set CHROMIUM to a browser binary`);
}

async function api(base, path, {seat, body, method = body ? 'POST' : 'GET'} = {}) {
  const response = await fetch(base + path, {
    method,
    signal: AbortSignal.timeout(15000),
    headers: {'Content-Type': 'application/json', ...(seat ? {'X-Seat-Token': seat} : {})},
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  assert.ok(response.ok, `${path} answered ${response.status}: ${text.slice(0, 140)}`);
  return text ? JSON.parse(text) : null;
}

// A screenshot is written beside its final name and moved over it only when all three exist, so
// a run that dies halfway leaves the previous set untouched
async function stage(path) {
  // The temporary name keeps the png extension: the browser picks the image format from it
  const temporary = `${path}.part.png`;
  return {temporary, settle: () => rename(temporary, path)};
}

const options = parse(process.argv.slice(2));
const staged = [];
let browser;
try {
  const context = await playwright().chromium.launch({
    executablePath: onPath(process.env.CHROMIUM || 'chromium'),
    args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage', '--force-color-profile=srgb'],
  }).then(instance => {
    browser = instance;
    return instance.newContext({
      viewport: {width: 1180, height: 860},
      deviceScaleFactor: 1,
      colorScheme: 'light',
      reducedMotion: 'no-preference',
      locale: options.lang === 'en' ? 'en-US' : 'ru-RU',
    });
  });

  // A stored theme or language of another tab would change the picture between runs
  await context.addInitScript(language => {
    try {
      localStorage.setItem('truck-kun-tower:theme', 'light');
      localStorage.setItem('truck-kun-tower:lang', language);
    } catch {}
  }, options.lang);

  const cover = await context.newPage();
  const faults = [];
  cover.on('pageerror', error => faults.push(error.message));
  await cover.goto(options.url, {waitUntil: 'networkidle'});
  await cover.locator('#auth').waitFor({timeout: 20000});
  await cover.waitForTimeout(400);
  const coverShot = await stage(join(options.out, 'cover.png'));
  await cover.screenshot({path: coverShot.temporary, fullPage: true});
  staged.push(coverShot);

  const host = await api(options.url, '/api/duels', {body: {side: 'defender'}});
  const room = `/api/duels/${host.id}`;
  const visitor = await api(options.url, `${room}/join`, {body: {side: 'attacker'}});
  const state = await api(options.url, `${room}/state`, {seat: host.seatToken});

  const board = await context.newPage();
  board.on('pageerror', error => faults.push(error.message));
  await board.addInitScript(({id, token}) => sessionStorage.setItem(`duel:${id}`, token),
    {id: host.id, token: host.seatToken});
  await board.goto(`${options.url}/?room=${host.id}`, {waitUntil: 'networkidle'});
  await board.locator('#actions button[data-action="frost"]').waitFor({timeout: 20000});

  // A frost tower off the road is the picture the README wants: it belongs to the board without
  // hiding the maze, and the fence on its offered cell shows the trap row at the same time
  const road = new Set([...state.paths, ...state.alternatePaths].flat());
  const width = state.width;
  const wall = state.paths[0].slice(1, -1).flatMap(cell => [cell - 1, cell + 1, cell - width, cell + width])
    .filter(cell => cell >= 0 && cell < width * state.height && !road.has(cell));
  assert.ok(wall.length > 0, 'a maze with no cell beside its road cannot hold the frost tower');
  for (const [action, cell] of [['frost', wall[0]], ['fence', state.fenceCells[0]]]) {
    await board.locator(`#actions button[data-action="${action}"]`).click();
    await board.locator(`#cells button[data-cell="${cell}"]`).click();
    await board.locator('#commit').click();
    await board.waitForTimeout(400);
  }
  await board.waitForTimeout(250);
  const planningShot = await stage(join(options.out, 'planning.png'));
  await board.screenshot({path: planningShot.temporary, fullPage: true});
  staged.push(planningShot);

  await api(options.url, `${room}/moves`, {body: {wave: 1, action: 'swarm', cell: state.paths[0][0]}, seat: visitor.seatToken});
  await api(options.url, `${room}/moves`, {body: {wave: 1, action: 'ready', cell: null}, seat: host.seatToken});
  await api(options.url, `${room}/moves`, {body: {wave: 1, action: 'ready', cell: null}, seat: visitor.seatToken});
  await board.waitForFunction(() => typeof replay !== 'undefined' && replay, undefined, {timeout: 20000});
  // Long enough for the swarm to be spread over the road, short enough that the wave still runs
  await board.waitForTimeout(4200);
  const battleShot = await stage(join(options.out, 'battle.png'));
  await board.screenshot({path: battleShot.temporary, fullPage: true});
  staged.push(battleShot);

  const problems = faults.filter(message => !/favicon|ERR_BLOCKED/.test(message));
  assert.equal(problems.length, 0, `the page raised ${problems[0]}`);
  for (const shot of staged) await shot.settle();
  console.log(`shots: cover, planning and battle written to ${options.out} in ${options.lang}`);
} catch (error) {
  await Promise.all(staged.map(shot => rm(shot.temporary, {force: true})));
  fail(error?.message ?? String(error));
} finally {
  await browser?.close();
}
