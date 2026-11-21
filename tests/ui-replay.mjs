import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';


const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const root = fileURLToPath(new URL('../wwwroot/', import.meta.url));
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || 'chromium',
  args: ['--js-flags=--max-old-space-size=256', '--disable-dev-shm-usage'] });
try {
  const page = await browser.newPage({viewport: {width: 1060, height: 860}, reducedMotion: 'no-preference'});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  // Fulfill all requests in the browser. This tests replay rendering, not the server simulation.
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    assert.equal(url.origin, 'http://fixture.invalid');
    const path = url.pathname === '/' ? '/index.html' : url.pathname;
    if (['/vendor/alpine/alpine.min.js', '/signalr.min.js'].includes(path)) return route.fulfill({body: ''});
    if (path === '/lobby.js') return route.fulfill({body: 'window.Tower = {on() {}, unwatchRoom() {}};'});
    if (path === '/sounds.js') return route.fulfill({body: 'window.cues = []; window.sounds = {play(name) { cues.push(name); }};'});
    return route.fulfill({path: join(root, path)});
  });
  await page.clock.install({time: new Date('2026-01-01T00:00:00Z')});
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
  await page.goto('http://fixture.invalid/');
  await page.evaluate(() => {
    el('app').hidden = true;
    el('game').hidden = false;
    const bolt = {type: 'bolt', cell: 22, hp: 10, max: 10};
    const spikes = {type: 'spikes', cell: 41, hp: 1, max: 3};
    const frost = {type: 'frost', cell: 55, hp: 6, max: 6};
    const fence = {cell: 43, hp: 6, max: 6};
    const apc = {id: 0, x: 1, y: 2, kind: 'apc', hp: 18, max: 30, frozen: false, facing: -1};
    const ranged = {id: 1, x: 2, y: 3, kind: 'ranged', hp: 8, max: 8, frozen: false};
    const other = {...ranged, id: 2, x: 3};
    const giant = {id: 3, x: 5, y: 3, kind: 'giant', hp: 12, max: 24, frozen: false};
    const troop = {id: 4, x: 5, y: 2, kind: 'balaclava', hp: 2, max: 4, frozen: false};
    const mobs = [apc, ranged, other, {...giant, x: 4}, troop, {...troop, id: 5}, {...troop, id: 6}];
    const mine = {type: 'mine', cell: 27, hp: 1, max: 1};
    const towers = [bolt, spikes, frost, mine];
    const base = {matchId: 'round1', width: 12, height: 6, joined: true, seat: 0, last: 0, done: false, locked: false,
      hp: 25, maxHp: 30, gold: 10, secondsLeft: 60, paths: [[24,25,26,27,28,29,30,31,32,33,34,35], [36,37,38,39,40,41,42,43,44,45,46,47,35]],
      alternatePaths: [], fenceCells: [43,41], towers, fences: [fence], orders: [], frames: [],
      prices: {bolt: 5, pulse: 6, cannon: 8, frost: 6, spikes: 3, fence: 4}};
    const idle = {hp: 25, buildings: towers, fences: [fence], mobs, shots: [], aims: [{cell: bolt.cell, x: 2, y: 3}], siege: []};
    const moving = {...idle, hp: 15, buildings: [{...bolt, hp: 7}, frost], fences: [{...fence, hp: 1}],
      mobs: [{...apc, x: 2, hp: 12, facing: 1}, ranged, other, giant, {...troop, id: 5}, {...troop, id: 6}, {...other, id: 7, kind: 'runner', x: 0, hp: 1, max: 1}],
      aims: [], siege: [{cell: bolt.cell, x: ranged.x, y: ranged.y, mobId: ranged.id}],
      shots: [{cell: spikes.cell, x: giant.x, y: giant.y}, {cell: mine.cell, x: 3, y: 2},
        {cell: bolt.cell, x: giant.x, y: giant.y}]};
    const still = {...moving, mobs: [...moving.mobs].reverse().map(m => m.id === 0 ? {...m, frozen: true} : m), shots: [], siege: []};
    window.base = base;
    window.wave = {...base, last: 1, hp: 15, towers: moving.buildings, fences: moving.fences,
      replayMs: 3000, replayLeftMs: 3000, frames: [idle, moving, still]};
    window.mob = id => document.querySelector(`.battle-mob[data-id="${id}"]`);
    window.check = (value, msg) => { if (!value) throw Error(msg); };
    window.hasRatio = (node, expected) => {
      const bar = node.matches('.battle-hp') ? node : node.querySelector('.battle-hp');
      const ratio = bar.querySelector('.battle-hp-fill').width.baseVal.value / bar.querySelector('.battle-hp-track').width.baseVal.value;
      return Math.abs(ratio - expected) < 1e-6;
    };
    accept({...base, joined: false});
    check(!cues.includes('matchStart'), 'A waiting room must stay silent');
    accept(base);
    check(hasRatio(el('map-fences'), 1), 'Full fence must have a full bar');
    accept({...base, fences: [{cell: fence.cell, hp: 4}]});
    check(el('map-fences').querySelector('text')?.textContent === '4' &&
      !el('map-fences').querySelector('.battle-hp-track'), 'Old fence must show HP, not an empty bar');
    accept({...base, fences: [{...fence, hp: 2, max: 0}]});
    check(el('map-fences').querySelector('text')?.textContent === '2' &&
      !el('map-fences').querySelector('.battle-hp-track'), 'Invalid max must show numeric HP');
    accept(base);
    check(onRoad('mine') && onRoad('spikes') && !onRoad('bolt'), 'Mine preview must use road cells');
    accept(wave);
    check(cues.filter(name => name === 'matchStart').length === 1, 'The match cue must play once, not on each update or wave');
    window.retainedTower = document.querySelector('#map-towers .tower');
    window.retainedAngle = retainedTower.dataset.angle;
    check(retainedAngle !== '0', 'Initial aim did not turn the tower');
    window.carrier = mob(0);
    window.passenger = mob(5);
    check(carrier && mob(7) === null, 'Spawn nodes do not match frame zero');
    check(carrier.getAnimations({subtree: true}).length === 0, 'New NPCs must not tween');
    check(hasRatio(carrier, 18 / 30), 'Wrong initial NPC HP');
    window.facingLeft = node => node.querySelector('.mob-body use').getCTM().a < 0;
    check(facingLeft(carrier), 'A left-facing vehicle did not mirror');
    check(carrier.querySelector('.battle-hp').getCTM().a > 0, 'The vehicle HP bar mirrored with it');
    check(!el('map-fences').getAnimations({subtree: true}).length, 'A full-health fence shook');
    window.passengerPosition = getComputedStyle(passenger).transform;
  });
  console.log('PASS: full fence, numeric HP without a valid max, spawn and initial HP');
  await page.clock.runFor(1000);
  assert.deepEqual(await page.evaluate(() => cues.filter(name => ['apcMove', 'mine', 'siege'].includes(name)).sort()), ['apcMove', 'mine', 'siege']);
  await page.evaluate(async () => {
    // The tower keeps its node, but it turns to the slime it struck in this frame, so the angle moves
    check(retainedTower === document.querySelector('#map-towers .tower'), 'Removing traps replaced the tower node');
    check(retainedTower.dataset.angle !== retainedAngle && Number.isFinite(Number(retainedTower.dataset.angle)) &&
      retainedTower.dataset.angle !== '0', 'The retained tower must turn to its new target');
    check(carrier === mob(0) && passenger === mob(5), 'Frame replaced retained NPCs');
    check(!facingLeft(carrier), 'The vehicle did not turn back to face right');
    check(getComputedStyle(passenger).transform === passengerPosition, 'Passenger slid when an adjacent slot cleared');
    check(mob(7) && !mob(7).getAnimations({subtree: true}).length, 'New spawn has a movement animation');
    check(!document.querySelector('#map-keep .battle-hp'), 'The keep must not have an HP bar');
    check(el('hp').textContent === '15', 'The keep counter must show the current frame HP');
    check(hasRatio(carrier, 12 / 30) && hasRatio(document.querySelector('#map-fences'), 1 / 6),
      'HP does not reflect the battle frame');
    const bars = [...document.querySelectorAll('#map-structure-hp .battle-hp')];
    check(bars.length === 2 && bars.some(bar => hasRatio(bar, .7)), 'Destroyed and damaged structures have the wrong bars');
    check(document.querySelector('.apc-dust'), 'Missing dust on APC step');
    // Only the tower that fired draws a sight line; a spent trap draws its own burst, never a line
    check(document.querySelector('.spike-break') && document.querySelector('.mine-burst') &&
      document.querySelectorAll('.shot-line').length === 1, 'A spent trap must not replay as a sight line');
    check(mob(1).querySelector('.siege-muzzle') && !mob(2).querySelector('.siege-muzzle') &&
      document.querySelector('.siege-streak') && document.querySelector('.bolt-shot, .cannon-shot'),
      'Siege streak, tower sight line or mobId-specific muzzle is missing');
    const move = carrier.getAnimations()[0];
    const squash = carrier.querySelector('.mob-body').getAnimations()[0];
    check(move && squash, 'Step must interpolate and squash once');
    const fenceShake = el('map-fences').getAnimations({subtree: true})[0];
    const towerShake = retainedTower.closest('svg').getAnimations({subtree: true})
      .find(animation => animation.effect.getKeyframes().some(frame => frame.transform?.includes('translateX')));
    const keepShake = el('map-keep').getAnimations({subtree: true})[0];
    check(fenceShake && towerShake && keepShake, 'Damaged fence, tower and keep must each shake');
    check(!document.querySelector('[data-building="55:frost"]').getAnimations({subtree: true}).length,
      'An unharmed tower shook');
    const animations = document.querySelector('#map').getAnimations({subtree: true});
    for (const animation of animations) animation.pause();
    await Promise.all(animations.map(animation => animation.ready));
    for (const shake of [fenceShake, towerShake, keepShake]) shake.currentTime = shake.effect.getTiming().duration / 4;
    const offset = shake => new DOMMatrix(getComputedStyle(shake.effect.target).transform).e;
    check(offset(fenceShake) < 0 && offset(fenceShake) === offset(towerShake) && offset(towerShake) === offset(keepShake),
      'Structures did not move like the keep');
    check(!el('map-fences').querySelector('.battle-hp').getAnimations().length &&
      !fenceShake.effect.target.contains(el('map-fences').querySelector('.battle-hp')), 'The fence HP bar moved');
    move.currentTime = move.effect.getTiming().duration / 2;
    squash.currentTime = squash.effect.getTiming().duration * .38;
    const mid = new DOMMatrix(getComputedStyle(carrier).transform).e;
    check(mid > 60 && mid < 100, `No intermediate position: ${mid}`);
    const bar = carrier.querySelector('.battle-hp').getCTM();
    const world = carrier.getCTM();
    check(['a', 'b', 'c', 'd'].every(key => bar[key] === world[key]), 'HP bar skewed or squashed with sprite');
    for (const animation of animations.filter(a => a !== move && a !== squash)) {
      animation.currentTime = animation.effect.getTiming().duration * .16;
    }
  });
  console.log('PASS: retained nodes, animated step, tower angle, fence/tower/mob HP, APC dust, spike break and ranged FX');
  const board = page.locator('.board-frame');
  if (process.env.UI_OUTPUT) {
    await board.screenshot({path: join(process.env.UI_OUTPUT, 'battle-light.png')});
    await page.emulateMedia({colorScheme: 'dark'});
    await board.screenshot({path: join(process.env.UI_OUTPUT, 'battle-dark.png')});
  }
  await page.evaluate(() => {
    for (const animation of el('map').getAnimations({subtree: true})) animation.finish();
  });
  await page.clock.runFor(1000);
  await page.evaluate(() => {
    check(carrier === mob(0) && !carrier.getAnimations({subtree: true}).length, 'Stationary frame restarted a step');
    check(['map-fences', 'map-towers', 'map-keep'].every(id => !el(id).getAnimations({subtree: true}).length),
      'Unchanged HP restarted a shake');
    check(carrier.querySelector('.frozen') && !document.querySelector('.apc-dust, .spike-break, .siege-streak'), 'Transient FX leaked');
  });
  await page.clock.runFor(1000);
  await page.evaluate(() => {
    check(!replay && !carrier.isConnected && !document.querySelector('.battle-mob'), 'Replay exit leaked NPCs');
    accept({...base, matchId: 'round2'});
    accept({...wave, matchId: 'round2'});
    check(mob(0) && mob(0) !== carrier, 'New match reused an old NPC node');
  });
  await page.emulateMedia({reducedMotion: 'reduce'});
  await page.clock.runFor(1000);
  await page.evaluate(() => {
    check(!document.querySelector('#map').getAnimations({subtree: true}).length, 'Reduced motion still uses animations');
    check(new DOMMatrix(getComputedStyle(mob(0)).transform).e === 100 && hasRatio(mob(0), 12 / 30), 'Reduced motion lost position or HP');
    check(document.querySelector('.spike-break, .siege-streak'), 'Reduced motion lost battle events');
    roomId = 'fixture';
    leave(null);
    check(!replay && !el('map').children.length, 'Board exit left the replay mounted');
  });
  await page.clock.runFor(3000);
  await page.emulateMedia({reducedMotion: 'no-preference'});
  await page.evaluate(() => {
    const fresh = {...wave, matchId: 'round3', replayLeftMs: 1800};
    accept(fresh);
    check(mob(0) && !mob(0).getAnimations({subtree: true}).length, 'Late join invented an offscreen step');
    check(['map-fences', 'map-towers', 'map-keep'].every(id => !el(id).getAnimations({subtree: true}).length),
      'Late join replayed an offscreen impact');
    check(document.querySelector('.mine-burst'), 'Late join did not resolve the spent mine from stored frames');
    accept({...base, matchId: 'round4'});
    accept({...wave, matchId: 'round4', frames: [wave.frames[1]], replayMs: 1000, replayLeftMs: 1000});
    check(document.querySelector('.mine-burst'), 'First replay frame did not use the pre-wave mine');
  });
  const replayLink = page.getByRole('link', {name: /Watch match replay|Смотреть реплей матча/});
  await page.evaluate(() => {
    el('game').hidden = false;
    accept({...base, done: true, matchId: 'settled'});
  });
  await replayLink.waitFor({timeout: 5000});
  assert.equal(await replayLink.getAttribute('href'), '?replay=settled');
  assert.ok(await replayLink.evaluate(link => {
    const style = getComputedStyle(link);
    return link.getBoundingClientRect().height >= 44 && parseFloat(style.borderTopWidth) > 0 &&
      style.textDecorationLine === 'none';
  }), 'The replay link has a visible button frame and touch target');
  assert.deepEqual(errors, []);
  console.log('PASS: stationary frame, replay and board cleanup, rematch, reduced motion, late join and first-frame trap');
} finally {
  await browser.close();
}
