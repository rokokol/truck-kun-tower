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

const DEFAULT_URL = 'https://tower.rokokol.art';
const USAGE = `node tools/demo-gif.mjs [options]

  --url <url>     record a game other than ${DEFAULT_URL}
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
// One order of every attacker type costs 53 gold, and a wave pays 16, 17, 20, 24 and 29 without
// carrying the rest over, so no single wave can show all seven. The last wave holds the most, and
// the demo buys what that gold covers from the cheapest type up, so the frame shows the widest
// set one wave can pay for. The waves before it carry no order, and a wave with no order has no
// frame, so they cost a request each
const ATTACK = ['swarm', 'giant', 'smart', 'runner', 'armored', 'ranged', 'apc'];
import {fillDefence, LAST_WAVE} from './duel-setup.mjs';

// The panel speaks the language of the frames: its taglines are the script's own copy, while
// everything in the board beside them comes from the page
const TAGLINES = {
  en: ['Defend the maze', 'Send slimes to the gate'],
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
  position: fixed; inset: 0; display: grid;
  grid-template-columns: ${PANEL}px minmax(0, 1fr); grid-template-rows: minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  overflow: hidden; color: var(--ddlc-text); font: 16px/1.5 var(--ddlc-font-prose);
  background-color: var(--ddlc-ground);
  background-image: radial-gradient(var(--ddlc-dots) 1.5px, transparent 1.6px);
  background-size: 18px 18px;
}
#demo-panel {
  display: flex; flex-direction: column; justify-content: center;
  padding: 22px 14px; text-align: center;
  box-shadow: inset -2px 0 var(--ddlc-line);
}
#demo-block { display: grid; justify-items: center; gap: 14px; }
#demo-avatar { display: block; width: 128px; height: 128px; border-radius: 50%; }
#demo-brand { display: grid; gap: 4px; }
#demo-title { margin: 0; font: 900 20px/1.1 var(--ddlc-font-prose); text-wrap: balance; }
#demo-panel .kicker {
  margin: 0; padding: 0; text-wrap: balance;
  font: 700 9px/1.4 var(--ddlc-font-data); letter-spacing: .06em;
  text-transform: uppercase; color: var(--ddlc-muted);
}
#demo-panel .lines { display: grid; gap: 4px; }
#demo-panel .line { margin: 0; font: 700 13px/1.3 var(--ddlc-font-prose); text-wrap: balance; }
#demo-panel .badges { justify-content: center; gap: 5px; margin: 0; }
#demo-panel .badges li {
  font: 700 9px/1 var(--ddlc-font-data); letter-spacing: .04em; padding: 5px;
  white-space: nowrap;
}
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
  url: DEFAULT_URL,
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

// A duel is two guest seats on one maze: the defender fills four waves with towers, and the
// attacker sends five groups down the gates of the last one

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

// A duel is two guest seats on one maze: the defender fills four waves with towers, and the
// attacker sends five groups down the gates of the last one

// Read the brand and badges from the page so the GIF stays in step with the site's copy
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
          <img id="demo-avatar" alt="">
          <div id="demo-brand"><h1 id="demo-title"></h1><small class="kicker"></small></div>
          <div class="lines"><p class="line"></p><p class="line"></p></div>
          <ul class="badges"></ul>
        </div>
      </aside>
      <section id="demo-stage"><div id="demo-head"></div><div id="demo-board"></div></section>`;
    document.body.append(frame);
    frame.querySelector('#demo-avatar').src = pictures.avatar;
    frame.querySelector('#demo-title').textContent = document.title;
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
  // Decode the art before the first screenshot so the loop starts with a complete panel
  await page.evaluate(() => Promise.all(
    [...document.querySelectorAll('#demo-frame img')].map(node => node.decode())));
  await page.evaluate(() => document.fonts.ready);
  const air = await page.evaluate(() => {
    const panel = document.getElementById('demo-panel').getBoundingClientRect();
    const block = document.getElementById('demo-block').getBoundingClientRect();
    return {top: block.top - panel.top, bottom: panel.bottom - block.bottom};
  });
  sized.air = air;
  check(Math.min(air.top, air.bottom) >= 8, 'the brand content has no vertical margin');
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
  const bought = await fillDefence(options.url, room, host.seatToken, visitor.seatToken, opening)
    .catch(error => fail(error.message));
  const purse = await api(`${room}/state`, {seat: visitor.seatToken});
  const chosen = [];
  let spent = 0;
  for (const type of [...ATTACK].sort((a, b) => purse.prices[a] - purse.prices[b])) {
    const price = purse.prices[type];
    if (spent + price > purse.gold) continue;
    chosen.push(type);
    spent += price;
  }
  check(chosen.length >= 4,
    `one wave pays for only ${chosen.length} of the ${ATTACK.length} types at ${purse.gold} gold`);
  for (const [index, type] of chosen.entries()) {
    await api(`${room}/moves`, {seat: visitor.seatToken,
      body: {wave: LAST_WAVE, action: type, cell: gates[index % gates.length]}});
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
    // The towers were bought through the API over the earlier waves, so the frame opens on the
    // last wave with the defence already standing and no order left to place
    check(await page.locator('#orders li').count() >= 6,
      'the earlier waves left too few structures on the board for the demo');
    const {sized, boxes} = await composeFrame(page, pictures);
    await api(`${room}/moves`, {seat: visitor.seatToken,
      body: {wave: LAST_WAVE, action: 'ready', cell: null}});
    // The frame has hidden the game screen by now, so the last press goes to the element itself
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
    console.log(`wave ${wave.ticks} ticks, ${chosen.length} attacker orders, ` +
      `${bought} structures for the five waves' gold, replay ` +
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
