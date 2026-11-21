// The demo GIF is a repository artifact, so its recipe is a script here rather than a recording
// that lives in someone's scratch directory. One command replays a wave against a running game
// and writes docs/telegram-demo.gif, or fails loudly with nothing half-written
import {createHash} from 'node:crypto';
import {spawn} from 'node:child_process';
import {existsSync} from 'node:fs';
import {mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {tmpdir} from 'node:os';
import {join, relative, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const repo = fileURLToPath(new URL('..', import.meta.url));

const USAGE = `node tools/demo-gif.mjs [options]

  --url <url>     record a game other than https://tower.rokokol.art:8443
  --out <file>    write the GIF somewhere other than docs/telegram-demo.gif
  --lang <code>   ru or en, the language the frames are taken in (default en)
  --frames <dir>  keep the PNG frames in this directory
  --keep-frames   keep the frames in a temporary directory and print its path
  -h, --help      this text`;

// The frame the README and the Telegram banner already use, at two device pixels per CSS pixel:
// the downscale by ffmpeg is what keeps the smallest panel type readable in a 256-colour GIF
const FRAME = {width: 640, height: 360};
const PIXEL_RATIO = 2;
const PANEL = 218;
const STAGE_PAD = {top: 10, right: 14, bottom: 12, left: 14};
const HEAD = 28;
const GAP = 8;
const SMALLEST_CELL = 18;
// A wave replays its fight ticks at 270 ms each, so its length is the game's, not this script's:
// one wave runs 10 s and another 20 s on the same board. The loop is played over a fixed span
// instead, because a chat client shows 9-10 s best and the wrap has to come back to the wave's
// first frame, not into the middle of the fight
const LOOP_MS = 9700;
// Past this the whole wave stops being a demo and becomes a time-lapse
const PACE = [0.5, 3];
// The server caps a replay at 52 s, so anything past this is a hang and not a long wave
const CAPTURE_SECONDS = 60;
const READY_SECONDS = 20;
// One order of every attacker type costs 53 gold and the attacker holds 16, 22, 34 and 58 at the
// four first waves, so only the fourth wave can show all seven at once. The waves before it carry
// no order, and a wave with no order has no frame, so they cost a request each
const ATTACK = ['swarm', 'giant', 'smart', 'runner', 'armored', 'ranged', 'apc'];
const RECORDED_WAVE = 4;

// The panel speaks the language of the frames: its taglines are the script's own copy, while
// everything in the board beside them comes from the page
const TAGLINES = {
  en: ['The defender holds the maze', 'The attacker sends slimes to the gate'],
  ru: ['Защитник держит лабиринт', 'Атакующий ведёт слизней ко входу'],
};
const GENRE_BADGE = 'tower defence';

// The ids below are the game's own update targets: render() rewrites #status, #pips and #clock,
// and makeField() sizes #board, so the frame moves those nodes and never copies their content
const FRAME_CSS = `
html, body { margin: 0; height: 100%; overflow: hidden; }
main { max-width: none; margin: 0; padding: 0; }
header, #app, #error, #game { display: none !important; }
#demo-frame {
  position: fixed; inset: 0; display: grid; grid-template-columns: ${PANEL}px minmax(0, 1fr);
  overflow: hidden; color: var(--ddlc-text); font: 16px/1.5 var(--ddlc-font-prose);
  background-color: var(--ddlc-ground);
  background-image: radial-gradient(var(--ddlc-dots) 1.5px, transparent 1.6px);
  background-size: 18px 18px;
}
#demo-panel {
  display: flex; flex-direction: column; justify-content: center;
  padding: 22px 18px; border-right: 2px solid var(--ddlc-line);
}
#demo-block { display: grid; gap: 10px; }
#demo-lead { display: flex; align-items: center; gap: 10px; }
#demo-avatar { display: block; flex: none; width: 96px; height: 96px; border-radius: 50%; }
#demo-panel .kicker {
  display: block; margin: 0; padding: 0; min-width: 0; text-wrap: balance;
  font: 700 7px/1.5 var(--ddlc-font-data); letter-spacing: .06em;
  text-transform: uppercase; color: var(--ddlc-muted);
}
#demo-panel .lines { display: grid; gap: 4px; }
#demo-panel .line { margin: 0; font: 700 12.5px/1.35 var(--ddlc-font-prose); text-wrap: balance; }
#demo-panel .line.soft { font-weight: 650; color: var(--ddlc-muted); }
#demo-panel .badges { display: flex; flex-wrap: wrap; gap: 5px; margin: 4px 0 0; padding: 0; list-style: none; }
#demo-panel .badges li { font: 700 7.5px/1 var(--ddlc-font-data); letter-spacing: .08em; padding: 4px 5px; }
#demo-stage {
  display: grid; grid-template-rows: ${HEAD}px minmax(0, 1fr); gap: ${GAP}px; min-width: 0;
  padding: ${STAGE_PAD.top}px ${STAGE_PAD.right}px ${STAGE_PAD.bottom}px ${STAGE_PAD.left}px;
}
#demo-head { display: flex; align-items: center; gap: 10px; min-width: 0; }
#demo-frame #status { margin: 0; font-size: 20px; line-height: 24px; white-space: nowrap; }
#demo-frame .pips { margin: 0; gap: 5px; flex: none; }
#demo-frame .pips i { width: 18px; height: 5px; }
#demo-frame .pips i.now { animation: none; }
#demo-frame .clock { margin: 0 0 0 auto; }
#demo-board { display: grid; place-items: center; min-width: 0; min-height: 0; }
#demo-board .board-scroll { display: grid; place-items: center; border: 0; background: transparent; overflow: visible; }
#demo-frame #board { min-width: 0; outline: 1px solid var(--ddlc-line); outline-offset: 0; }
`;

const options = {
  url: 'https://tower.rokokol.art:8443',
  out: join(repo, 'docs/telegram-demo.gif'),
  // The art is English, so the demo is too: a chat client shows this frame beside a caption that
  // is not ours, and a Russian panel next to an English caption reads as two products
  lang: 'en',
  frames: null,
  keepFrames: false,
};

const LANGUAGES = ['en', 'ru'];

function fail(problem) {
  throw new Error(problem);
}

function check(condition, problem) {
  if (!condition) fail(problem);
}

function parseArguments(argv) {
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    if (flag === '-h' || flag === '--help') {
      console.log(USAGE);
      process.exit(0);
    } else if (flag === '--keep-frames') {
      options.keepFrames = true;
    } else if (['--url', '--out', '--frames', '--lang'].includes(flag)) {
      const value = argv[i += 1];
      check(Boolean(value), `${flag} needs a value\n\n${USAGE}`);
      const name = flag.slice(2);
      check(name === 'lang' ? LANGUAGES.includes(value) : true,
        `--lang takes ${LANGUAGES.join(' or ')}, not ${value}`);
      options[name] = name === 'url' ? value.replace(/\/+$/, '')
        : name === 'lang' ? value : resolve(value);
    } else {
      fail(`unknown option ${flag}\n\n${USAGE}`);
    }
  }
}

// Playwright is loaded here rather than at import time, so --help and a bad flag answer without
// the module installed, and a missing module names the variable that fixes it
function playwright() {
  const name = process.env.PLAYWRIGHT_CORE || 'playwright-core';
  try {
    return createRequire(import.meta.url)(name);
  } catch {
    return fail(`cannot load ${name}; set PLAYWRIGHT_CORE to a playwright-core install`);
  }
}

// Playwright reads executablePath as a file and never searches PATH, so a bare name such as the
// tests' default `chromium` has to be resolved here
function onPath(name) {
  if (name.includes('/')) return name;
  for (const dir of (process.env.PATH ?? '').split(':')) {
    const candidate = join(dir, name);
    if (existsSync(candidate)) return candidate;
  }
  return name;
}

async function run(command, args) {
  const child = spawn(command, args, {stdio: ['ignore', 'pipe', 'pipe']});
  let output = '';
  child.stdout.on('data', chunk => { output += chunk; });
  child.stderr.on('data', chunk => { output += chunk; });
  const code = await new Promise((done, reject) => {
    child.on('error', reject);
    child.on('close', done);
  });
  if (code !== 0) fail(`${command} exited ${code}\n${output.trim().slice(-1200)}`);
  return output;
}

async function api(path, {seat, body, method = body ? 'POST' : 'GET'} = {}) {
  let response;
  try {
    response = await fetch(options.url + path, {
      method,
      signal: AbortSignal.timeout(15000),
      headers: {'Content-Type': 'application/json', ...(seat ? {'X-Seat-Token': seat} : {})},
      body: body && JSON.stringify(body),
    });
  } catch (cause) {
    fail(`the game at ${options.url} did not answer ${method} ${path}: ${cause.cause?.message ?? cause.message}`);
  }
  const text = await response.text();
  if (!response.ok) fail(`${method} ${path} answered ${response.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : null;
}

// Towers beside the last cells before the keep fire at every mob, because all three roads meet
// there. The fence and the spikes stand on the road itself, so the wave holds at the last step
function defencePlan(opening) {
  const {width, height, paths} = opening;
  const route = new Set([...paths, ...opening.alternatePaths].flat());
  const beside = cell => {
    const x = cell % width, y = (cell - x) / width;
    return [[0, -1], [0, 1], [-1, 0], [1, 0]]
      .map(([dx, dy]) => [x + dx, y + dy])
      .filter(([nx, ny]) => nx >= 0 && ny >= 0 && nx < width && ny < height)
      .map(([nx, ny]) => ny * width + nx);
  };
  const approach = [...new Set(paths.flatMap(path => path.slice(-4, -1)))];
  const towers = [];
  for (const cell of approach) {
    for (const wall of beside(cell)) {
      if (!route.has(wall) && !towers.includes(wall)) towers.push(wall);
    }
  }
  check(towers.length >= 3,
    `only ${towers.length} walls stand beside the road to the keep, and the defence holds three towers`);
  const orders = [['bolt', towers[0]], ['pulse', towers[1]], ['frost', towers[2]],
    ['fence', opening.fenceCells[0]], ['spikes', paths[0].at(-2)]];
  return {orders, bill: orders.reduce((sum, [type]) => sum + opening.prices[type], 0)};
}

// A duel is two guest seats on one maze: the defender places a tower on a wall beside the first
// road, and the attacker sends one order of every type down the gates
// docs/ is not served by the game, so the avatar travels into the page as a data URL
async function readPictures() {
  const pictures = {};
  for (const [name, file] of [['avatar', 'telegram-avatar.png']]) {
    const path = join(repo, 'docs', file);
    let bytes;
    try {
      bytes = await readFile(path);
    } catch {
      fail(`cannot read ${path}, which the brand panel shows`);
    }
    pictures[name] = `data:image/png;base64,${bytes.toString('base64')}`;
  }
  return pictures;
}

async function openDuel() {
  const host = await api('/api/duels', {body: {side: 'defender'}});
  const room = `/api/duels/${host.id}`;
  const visitor = await api(`${room}/join`, {body: {side: 'attacker'}});
  const state = await api(`${room}/state`, {seat: host.seatToken});
  const gates = [...new Set(state.paths.map(path => path[0]))];
  check(gates.length >= 2, 'the maze offers one gate, so every unit would walk one road');
  return {host, visitor, room, state, gates};
}

// A wave with no order has no frames, so it resolves before the reply arrives: three of those buy
// the attacker the gold for the recorded one
async function playQuietWaves(room, host, visitor) {
  for (let wave = 1; wave <= RECORDED_WAVE - 1; wave += 1) {
    await api(`${room}/moves`, {seat: visitor.seatToken, body: {wave, action: 'ready', cell: null}});
    const after = await api(`${room}/moves`,
      {seat: host.seatToken, body: {wave, action: 'ready', cell: null}});
    check(after.last === wave, `wave ${wave} closed at wave ${after.last}`);
    check(!after.done, `the game ended at wave ${wave}, before the wave that shows every unit`);
  }
  return api(`${room}/state`, {seat: visitor.seatToken});
}

// The panel takes its words from the page, so a change of the site's copy reaches the GIF: the
// badge list and the kicker are the site's, and only the taglines and the badge that names the
// genre are the frame's own. The avatar is the repository's own picture, inlined because a docs/
// file is not served by the game
async function composeFrame(page, pictures) {
  await page.addStyleTag({content: FRAME_CSS});
  const sized = await page.evaluate(({taglines, pictures, badge}) => {
    const pick = selector => {
      const node = document.querySelector(selector);
      if (!node) throw new Error(`the page has no ${selector} to take the frame's copy from`);
      return node;
    };
    const frame = document.createElement('div');
    frame.id = 'demo-frame';
    frame.innerHTML = `<aside id="demo-panel">
        <div id="demo-block">
          <div id="demo-lead"><img id="demo-avatar" alt=""><small class="kicker"></small></div>
          <div class="lines"><p class="line"></p><p class="line soft"></p></div>
          <ul class="badges"></ul>
        </div>
      </aside>
      <section id="demo-stage"><div id="demo-head"></div><div id="demo-board"></div></section>`;
    document.body.append(frame);
    frame.querySelector('#demo-avatar').src = pictures.avatar;
    frame.querySelector('.kicker').textContent = pick('#auth .kicker').textContent.trim();
    const lines = frame.querySelectorAll('.line');
    lines[0].textContent = taglines[0];
    lines[1].textContent = taglines[1];
    const words = [...document.querySelectorAll('#auth .badges li')].map(node => node.textContent.trim());
    words[words.length - 1] = badge;
    frame.querySelector('.badges').append(...words.map(word => {
      const item = document.createElement('li');
      item.textContent = word;
      return item;
    }));
    frame.querySelector('#demo-head').append(document.getElementById('status'),
      document.getElementById('pips'), document.getElementById('clock'));
    frame.querySelector('#demo-board').append(document.querySelector('.board-scroll'));
    const board = document.getElementById('board');
    const cols = Number(board.style.getPropertyValue('--cols'));
    const rows = Number(board.style.getPropertyValue('--rows'));
    // aspect-ratio cannot hold when both width and height are clamped, so the box is measured
    // here and set in whole pixels, which also keeps one board cell on whole pixels
    const room = document.getElementById('demo-board').getBoundingClientRect();
    const cell = Math.floor(Math.min(room.width / cols, room.height / rows));
    board.style.width = `${cols * cell}px`;
    board.style.height = `${rows * cell}px`;
    return {cols, rows, cell};
  }, {taglines: TAGLINES[options.lang], pictures, badge: GENRE_BADGE});
  // An image that has not decoded yet reserves no width, so without this the block would be
  // measured before the pictures take their place and would leave the middle on its own
  await page.evaluate(() => Promise.all(
    [...document.querySelectorAll('#demo-frame img')].map(node => node.decode())));
  const air = await page.evaluate(() => {
    const panel = document.getElementById('demo-panel').getBoundingClientRect();
    const block = document.getElementById('demo-block').getBoundingClientRect();
    return {top: block.top - panel.top, bottom: panel.bottom - block.bottom};
  });
  sized.air = air;
  await page.evaluate(() => document.fonts.ready);
  check(sized.cell >= SMALLEST_CELL,
    `the board would draw a ${sized.cell}px cell in a 640x360 frame; the panel leaves too little room`);
  // A frame that crops the board is the failure this frame exists to prevent, so every box is
  // measured after layout and held to the viewport
  const boxes = await page.evaluate(() => {
    const box = selector => {
      const {x, y, width, height} = document.querySelector(selector).getBoundingClientRect();
      return {x, y, width, height};
    };
    return {panel: box('#demo-panel'), head: box('#demo-head'), board: box('#board'),
      status: box('#status'), pips: box('#pips')};
  });
  const {width, height} = FRAME;
  check(Math.abs(boxes.panel.x) < 0.01 && Math.abs(boxes.panel.y) < 0.01,
    'the brand panel does not start in the top left corner');
  check(Math.abs(boxes.panel.height - height) < 0.5, `the brand panel is ${boxes.panel.height}px tall`);
  check(boxes.board.x >= boxes.panel.width && boxes.board.x + boxes.board.width <= width,
    'the board runs past the right edge of the frame');
  check(boxes.board.y >= boxes.head.y + boxes.head.height &&
    boxes.board.y + boxes.board.height <= height,
    'the board runs past the top or bottom edge of the frame');
  const shape = boxes.board.width / boxes.board.height;
  check(Math.abs(shape - sized.cols / sized.rows) < 0.01 * shape,
    `the board is ${shape.toFixed(3)} wide for a ${sized.cols}x${sized.rows} maze`);
  check(Math.abs(sized.air.top - sized.air.bottom) < 2,
    `the panel's block sits ${sized.air.top.toFixed(1)}px from the top and ` +
    `${sized.air.bottom.toFixed(1)}px from the bottom, not centred`);
  for (const name of ['status', 'pips']) {
    const box = boxes[name];
    check(box.y >= boxes.head.y - 0.5 && box.y + box.height <= boxes.head.y + boxes.head.height + 0.5,
      `the ${name} line does not sit inside the head row`);
  }
  // The first shot of a fresh layer tree waits for a full raster and returns seconds later, so it
  // is taken here and thrown away: its content would be the middle of the wave, not its start
  await page.screenshot({clip: {x: 0, y: 0, ...FRAME}});
  return {sized, boxes};
}

async function capture(page, framesDir) {
  const frames = [];
  let last = '';
  let still = 0;
  const deadline = Date.now() + CAPTURE_SECONDS * 1000;
  let ended = false;
  while (Date.now() < deadline) {
    const shot = await page.screenshot({clip: {x: 0, y: 0, ...FRAME}});
    const digest = createHash('sha1').update(shot).digest('hex');
    const file = join(framesDir, `f${String(frames.length).padStart(4, '0')}.png`);
    // A repeated shot replays as a freeze, so it never earns a frame of its own
    const fresh = digest !== last;
    if (fresh) {
      await writeFile(file, shot);
      frames.push(file);
      last = digest;
    } else {
      still += 1;
    }
    // A wave that ends the game leaves no planning screen behind, so the replay itself is the end
    if (await page.evaluate(() => !replay)) {
      ended = true;
      if (fresh) {
        frames.pop();
        await rm(file, {force: true});
      }
      break;
    }
  }
  check(ended, `the replay did not finish within ${CAPTURE_SECONDS} s`);
  check(frames.length >= 20, `the replay left only ${frames.length} frames to build a GIF from`);
  return {frames, still};
}

async function main() {
  parseArguments(process.argv.slice(2));
  const staging = `${options.out}.tmp`;
  const framesDir = options.frames
    ? options.frames
    : await mkdtemp(join(tmpdir(), 'tower-gif-'));
  if (options.frames) {
    await mkdir(options.frames, {recursive: true});
    // ffmpeg reads the directory in order, so a leftover frame from an older run would slip in
    check((await readdir(options.frames)).length === 0,
      `${options.frames} is not empty; point --frames at a new directory`);
  }
  const keepFrames = options.keepFrames || Boolean(options.frames);
  const pictures = await readPictures();
  const {host, visitor, room, state: opening, gates} = await openDuel();
  const defence = defencePlan(opening);
  const purse = await playQuietWaves(room, host, visitor);
  const bill = ATTACK.reduce((sum, type) => sum + purse.prices[type], 0);
  check(purse.gold >= bill,
    `the attacker holds ${purse.gold} gold at wave ${RECORDED_WAVE}, and one of every unit costs ${bill}`);
  for (const [index, type] of ATTACK.entries()) {
    await api(`${room}/moves`, {seat: visitor.seatToken,
      body: {wave: RECORDED_WAVE, action: type, cell: gates[index % gates.length]}});
  }
  let browser;
  try {
    browser = await playwright().chromium.launch({
      executablePath: onPath(process.env.CHROMIUM || 'chromium'),
      args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage',
        '--force-color-profile=srgb'],
    });
    const context = await browser.newContext({
      viewport: FRAME,
      deviceScaleFactor: PIXEL_RATIO,
      colorScheme: 'light',
      // The battle stops interpolating under reduce, so the GIF would show stops, not a walk
      reducedMotion: 'no-preference',
      locale: options.lang === 'en' ? 'en-US' : 'ru-RU',
    });
    // A stored theme or language of another tab would change the frame between runs
    await context.addInitScript(language => {
      try {
        localStorage.setItem('truck-kun-tower:theme', 'light');
        localStorage.setItem('truck-kun-tower:lang', language);
      } catch {}
    }, options.lang);
    const page = await context.newPage();
    const faults = [];
    page.on('pageerror', error => faults.push(error.message));
    await page.addInitScript(({id, token}) => sessionStorage.setItem(`duel:${id}`, token),
      {id: host.id, token: host.seatToken});
    await page.goto(`${options.url}/?room=${encodeURIComponent(host.id)}`, {waitUntil: 'networkidle'});
    const bolt = page.locator('#actions button[data-action="bolt"]');
    await bolt.waitFor({timeout: 15000}).catch(() => {
      fail(`no tower appeared at ${options.url}; check the address and that the board loads`);
    });
    const gold = await page.locator('#budget').textContent();
    check(await bolt.isEnabled(),
      `the bolt tower cannot be bought: the order is disabled at ${gold} gold`);
    check(Number(gold) >= defence.bill,
      `the defender holds ${gold} gold at wave ${RECORDED_WAVE}, and the defence costs ${defence.bill}`);
    let ordered = 0;
    const build = async (action, cell) => {
      const target = page.locator(`#cells button[data-cell="${cell}"]`);
      check(await target.count() > 0,
        `the board offers no cell ${cell}, so the ${action} order has nowhere to go`);
      await page.locator(`#actions button[data-action="${action}"]`).click();
      await target.click();
      check(await page.locator('#commit').isEnabled(),
        `the ${action} order on cell ${cell} never became ready to commit`);
      await page.locator('#commit').click();
      ordered += 1;
      await page.waitForFunction(count => document.querySelectorAll('#orders li').length === count,
        ordered, {timeout: 5000}).catch(() => {
        fail(`the ${action} order did not reach the plan: the board refused cell ${cell}`);
      });
    };
    for (const [action, cell] of defence.orders) await build(action, cell);
    const {sized, boxes} = await composeFrame(page, pictures);
    await api(`${room}/moves`, {seat: visitor.seatToken,
      body: {wave: RECORDED_WAVE, action: 'ready', cell: null}});
    // The frame has hidden the game screen by now, so the last press goes to the element itself;
    // every purchase above went through the real buttons
    await page.evaluate(() => document.getElementById('ready').click());
    const wave = await page.waitForFunction(() => typeof replay !== 'undefined' && replay
      ? {replayMs: replay.snapshot.replayMs, ticks: replay.snapshot.frames.length}
      : false, undefined, {timeout: READY_SECONDS * 1000, polling: 'raf'})
      .then(handle => handle.jsonValue(), () => null);
    if (!wave) fail(`the page never started a replay within ${READY_SECONDS} s of both sides saying ready`);
    const scale = wave.replayMs / LOOP_MS;
    check(scale >= PACE[0] && scale <= PACE[1],
      `the wave replays in ${(wave.replayMs / 1000).toFixed(2)} s, which a ${LOOP_MS / 1000} s loop ` +
      `can only hold at x${scale.toFixed(2)}; run the command again for another wave`);
    await page.waitForFunction(() => document.querySelectorAll('#map-mobs .battle-mob').length > 0,
      undefined, {timeout: 10000}).catch(() => {
      fail('the replay started but the board never drew a mob, so there is no fight to record');
    });
    const started = Date.now();
    const {frames, still} = await capture(page, framesDir);
    const filmed = Date.now() - started;
    check(faults.length === 0, `the page reported an error: ${faults[0]}`);
    await browser.close();
    browser = undefined;
    // palettegen buffers every frame it sees, so it reads the scaled board and not 1280x720
    const rate = `${frames.length * 1000}/${LOOP_MS}`;
    await run('ffmpeg', ['-y', '-hide_banner', '-loglevel', 'error', '-threads', '1',
      '-framerate', rate, '-i', join(framesDir, 'f%04d.png'),
      '-filter_complex',
      '[0:v]scale=640:360:flags=lanczos,split[board][palette];' +
      '[palette]palettegen=max_colors=256:stats_mode=diff[colors];' +
      '[board][colors]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle',
      '-f', 'gif', '-loop', '0', staging]);
    const probe = JSON.parse(await run('ffprobe', ['-v', 'error', '-select_streams', 'v:0',
      '-count_frames', '-show_entries', 'stream=width,height,nb_read_frames',
      '-show_entries', 'format=duration,size', '-of', 'json', staging]));
    const [stream] = probe.streams;
    const duration = Number(probe.format.duration);
    check(stream.width === FRAME.width && stream.height === FRAME.height,
      `ffprobe reports ${stream.width}x${stream.height}; the demo must stay 640x360`);
    check(Number(stream.nb_read_frames) === frames.length,
      `the GIF holds ${stream.nb_read_frames} frames, not the ${frames.length} that were captured`);
    check(duration >= 9 && duration <= 10,
      `the GIF runs ${duration.toFixed(2)} s; a chat client needs 9 to 10 s`);
    // The README shows this file, so a run that fails half way must leave the old one intact
    await rename(staging, options.out);
    const kib = Math.round(Number(probe.format.size) / 1024);
    console.log(`${relative(repo, options.out)}  ${stream.width}x${stream.height}  ` +
      `${stream.nb_read_frames} frames  ${duration.toFixed(2)} s  ${kib} KiB`);
    console.log(`wave ${wave.ticks} ticks, ${ATTACK.length} attacker orders, ` +
      `${defence.orders.length} defence orders for ${defence.bill} gold, replay ` +
      `${(wave.replayMs / 1000).toFixed(2)} s, filmed ${(filmed / 1000).toFixed(2)} s, ` +
      `played over ${(LOOP_MS / 1000).toFixed(2)} s (x${scale.toFixed(2)}), ` +
      `${still} identical frames dropped`);
    console.log(`board ${sized.cols}x${sized.rows} at ${sized.cell}px, ` +
      `box ${boxes.board.width}x${boxes.board.height} at ${boxes.board.x},${boxes.board.y}, ` +
      `panel air ${sized.air.top.toFixed(1)}/${sized.air.bottom.toFixed(1)}`);
    if (keepFrames) console.log(`frames kept in ${framesDir}`);
  } finally {
    await browser?.close();
    await api(room, {method: 'DELETE', seat: host.seatToken}).catch(() => {});
    await rm(staging, {force: true});
    if (!keepFrames) await rm(framesDir, {recursive: true, force: true});
  }
}

main().catch(error => {
  console.error(`demo-gif: ${error.message}`);
  process.exit(1);
});
