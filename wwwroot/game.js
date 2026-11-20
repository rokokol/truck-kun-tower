// The board of one room. It owns #game and nothing else: the auth screen and the lobby
// live in lobby.js, and the two scripts agree on the tower:screen event.

const el = id => document.getElementById(id);
const waves = 5;
const tile = 40;
const towerSize = 26;
const { t: tr, message, feedback } = i18n;

// roomId is the whole identity of a game: the account travels in the bearer,
// the guest half in the seat token, and both are asked of Tower on every call
let roomId = null, seatToken = null;
let state, field, action, cell, replay, errorNote, copyTimer;
let copyNote = 'copyLink';
let generation = 0;
let seen = 0, sequence = 0, sending = false, timer = null, clockTimer = null, quiet = 0;

const trap = type => type === 'spikes' || type === 'mine';
const onRoad = type => type === 'fence' || trap(type);
const defending = () => state?.seat === 0;
const types = () => defending()
  ? ['bolt', 'pulse', 'cannon', 'frost', 'spikes', 'mine', 'fence']
  : ['swarm', 'giant', 'smart', 'runner', 'armored', 'ranged', 'apc'];
const planning = () => Boolean(state?.joined && !state.done && !state.locked && !replay && !sending && remaining() > 0);
const route = id => field.routes.get(id);
const entry = id => tr('gate', { index: field.entries.indexOf(id) + 1 });
const coords = id => [id % state.width, Math.floor(id / state.width)];
const center = (x, y) => [(x + .5) * tile, (y + .5) * tile];
const point = id => center(...coords(id));
const sprite = (id, x, y, size) => `<use href="actors.svg#${id}" x="${x - size / 2}" y="${y - size / 2}" width="${size}" height="${size}"/>`;
const fare = cost => `<span class="gold">${Number(cost)} <svg class="coin" aria-hidden="true" viewBox="0 0 32 32">${sprite('coin', 16, 16, 32)}</svg><span class="gold-unit"> ${tr('goldUnit')}</span></span>`;
const polyline = (path, cls) => `<polyline class="${cls}" points="${path.map(id => point(id).join(',')).join(' ')}"/>`;

const room = path => `/api/duels/${encodeURIComponent(roomId)}/${path}`;
const call = (path, options = {}) => Tower.request(path, { ...options, seat: seatToken });
function paintError() {
  el('error-detail').textContent = i18n.text(errorNote);
  el('error').hidden = !errorNote;
}
const fail = error => { errorNote = feedback(error); paintError(); };

function makeField() {
  const {width, height, paths, alternatePaths, fenceCells} = state;
  const allPaths = [...paths, ...alternatePaths];
  const pathsSet = new Set(allPaths.flat());
  const routes = new Map(paths.map(path => [path[0], path]));
  const entries = [...routes.keys()];
  const keep = paths[0].at(-1);
  const walls = Array.from({length: width * height}, (_, id) => id).filter(id => {
    if (pathsSet.has(id)) return false;
    const x = id % width;
    return (x > 0 && pathsSet.has(id - 1)) || (x < width - 1 && pathsSet.has(id + 1)) ||
      pathsSet.has(id - width) || pathsSet.has(id + width);
  });
  const road = [...pathsSet].map(id => {
    const [x, y] = coords(id);
    return `<rect class="road-tile" x="${x * tile}" y="${y * tile}" width="${tile}" height="${tile}"/>`;
  }).join('') + allPaths.map(path => polyline(path, 'road-line')).join('');
  const markers = entries.map((id, i) => {
    const [x, y] = point(id);
    return `<g class="entry-marker"><circle cx="${x}" cy="${y}" r="12"/><text x="${x}" y="${y}">${i + 1}</text></g>`;
  }).join('');
  const alternates = new Map(entries.map(id => [id, alternatePaths.filter(path => path[0] === id)]));
  field = {routes, entries, keep, road, markers, alternates};
  el('board').style.setProperty('--cols', width);
  el('board').style.setProperty('--rows', height);
  el('board').style.aspectRatio = `${width} / ${height}`;
  el('map').setAttribute('viewBox', `0 0 ${width * tile} ${height * tile}`);
  // Towers keep their own layer, so a turn can be eased by CSS instead of redrawn
  clearBattleActors();
  el('map').innerHTML = `<g id="map-road">${road}</g><g id="map-markers">${markers}</g>` +
    `<g id="map-route"></g><g id="map-keep"></g><g id="map-ghost"></g><g id="map-fences"></g>` +
    `<g id="map-towers"></g><g id="map-dust"></g><g id="map-mobs"></g><g id="map-structure-hp"></g>` +
    `<g id="map-shots"></g><g id="map-hits"></g>`;
  el('cells').replaceChildren();
  // A swap rebuilds the buttons: the defender sees walls and the attacker sees gates
  for (const id of defending() ? [...walls, ...fenceCells] : entries) {
    const button = document.createElement('button');
    const [x, y] = coords(id);
    button.className = 'cell';
    button.dataset.cell = id;
    const fenceSite = defending() && pathsSet.has(id);
    button.dataset.kind = defending() ? (fenceSite ? 'fence' : 'wall') : 'entry';
    button.style.gridArea = `${y + 1} / ${x + 1}`;
    button.onclick = () => { if (button.disabled) return; cell = cell === id ? undefined : id; render(); };
    el('cells').append(button);
  }
  paintCells();
}

function paintCells() {
  for (const button of el('cells').children) {
    const id = Number(button.dataset.cell);
    const [x, y] = coords(id).map(value => value + 1);
    button.setAttribute('aria-label', defending()
      ? tr(button.dataset.kind === 'fence' ? 'roadCell' : 'wallCell', { x, y }) : entry(id));
  }
}

function aimAngle(x, y, target) {
  const [tx, ty] = center(target.x, target.y);
  return Math.atan2(ty - y, tx - x) * 180 / Math.PI;
}

const turning = type => type === 'bolt' || type === 'cannon';

const aimAt = (id, target) => target ? aimAngle(...point(id), target) : -35;
function towerGlyph(type, id, target) {
  const [x, y] = coords(id).map(n => n * tile);
  const body = sprite(type, tile / 2, tile / 2, towerSize);
  const angle = aimAt(id, target);
  return `<svg class="tower-cell" data-building="${id}:${type}" x="${x}" y="${y}" width="${tile}" height="${tile}" viewBox="0 0 ${tile} ${tile}">` +
    (turning(type) ? `<g class="tower" data-cell="${id}" data-angle="${angle}" style="transform:rotate(${angle}deg)">${body}</g>` : body) + '</svg>';
}

// Keep each live node so changes to its neighbors do not reset its turn.
function syncTowers(towers, targets) {
  const layer = el('map-towers');
  const old = new Map(Array.from(layer.children, node => [node.dataset.building, node]));
  for (const {type, cell: id} of towers) {
    const key = `${id}:${type}`;
    const glyph = old.get(key);
    const target = targets.get(id);
    if (!glyph) {
      layer.insertAdjacentHTML('beforeend', towerGlyph(type, id, target));
      continue;
    }
    old.delete(key);
    if (!turning(type) || !target) continue;
    const node = glyph.firstElementChild;
    const from = Number(node.dataset.angle);
    const turn = ((aimAt(id, target) - from + 180) % 360 + 360) % 360 - 180;
    if (turn === 0) continue;
    node.dataset.angle = from + turn;
    node.style.transform = `rotate(${from + turn}deg)`;
  }
  for (const glyph of old.values()) glyph.remove();
}

function shot(target, type, spent) {
  const [x, y] = point(target.cell);
  const [tx, ty] = center(target.x, target.y);
  const fly = `style="--flight:${Math.hypot(tx - x, ty - y)}px"`;
  if (type === 'pulse') {
    return `<g class="pulse-shot"><path d="M${x},${y}L${tx},${ty}"/><circle cx="${tx}" cy="${ty}" r="10"/></g>`;
  }
  if (type === 'frost') {
    return `<g class="frost-shot"><path d="M${x},${y}L${tx},${ty}"/><circle cx="${tx}" cy="${ty}" r="7"/></g>`;
  }
  if (type === 'spikes') {
    return `<g class="spike-shot${spent ? ' spike-break' : ''}" transform="translate(${tx} ${ty})">
      <circle r="10"/><path d="M-12 5l3-5 2 6M8 4l3-6 2 6M-2-9l3-5 2 5"/>
      ${spent ? '<path class="spike-fragments" d="M-15-5l-3-3M14-8l4-4M-2 13l-2 5M13 9l4 3"/>' : ''}
    </g>`;
  }
  if (type === 'mine') {
    return `<g class="mine-burst" transform="translate(${tx} ${ty})">
      <circle r="13"/><path d="m0-10 3 7 7 3-7 3-3 7-3-7-7-3 7-3zM-13-9l-3-3m29 3 3-3m-3 21 3 3m-29-3-3 3"/>
    </g>`;
  }
  // A tower hits at once, so it does not throw anything: it snaps a dashed sight line onto the
  // slime it struck, which stays true however the board is easing the sprite between cells
  if (type === 'cannon') {
    return `<g class="shot-line cannon-shot"><path d="M${x},${y}L${tx},${ty}"/><circle cx="${tx}" cy="${ty}" r="6"/><circle class="shot-core" cx="${tx}" cy="${ty}" r="2.4"/></g>`;
  }
  if (type !== 'bolt') return '';
  return `<g class="shot-line bolt-shot"><path d="M${x},${y}L${tx},${ty}"/><circle cx="${tx}" cy="${ty}" r="4.5"/></g>`;
}

const reducedBattleMotion = matchMedia('(prefers-reduced-motion: reduce)');
const mobNodes = new Map();
const troopOffsets = [[-8, 7], [8, 7], [0, -6]];
const translateMob = (x, y) => `translate(${x}px, ${y}px)`;
function healthIndicator(x, y, width, hp, max) {
  if (!Number.isFinite(hp) || hp < 0) return '';
  if (!Number.isFinite(max) || max <= 0) {
    return `<text class="battle-hp-amount" x="${x}" y="${y + 2}">${hp}</text>`;
  }
  const ratio = Math.min(1, hp / max);
  return `<g class="battle-hp${ratio <= 1 / 3 ? ' low' : ''}">
    <rect class="battle-hp-track" x="${x - width / 2}" y="${y}" width="${width}" height="3.5" rx="1.5"/>
    <rect class="battle-hp-fill" x="${x - width / 2}" y="${y}" width="${width * ratio}" height="3.5" rx="1.5"/>
  </g>`;
}

function clearBattleActors() {
  for (const id of ['map-mobs', 'map-dust', 'map-shots', 'map-hits']) el(id)?.replaceChildren();
  mobNodes.clear();
  el('map').style.removeProperty('--battle-frame');
}

reducedBattleMotion.addEventListener('change', () => {
  if (!reducedBattleMotion.matches) return;
  for (const {node, body} of mobNodes.values()) {
    for (const animation of [...node.getAnimations(), ...body.getAnimations()]) animation.cancel();
  }
});

function apcDust(x, y, dx, dy) {
  return `<g transform="translate(${x + dx * .23} ${y + dy * .23 + 7}) rotate(${Math.atan2(dy, dx) * 180 / Math.PI})">
    <g class="apc-dust"><circle cx="-5" cy="2" r="3.8"/><circle cx="-11" cy="-3" r="2.7"/>
      <circle cx="-15" cy="3" r="2"/></g>
  </g>`;
}

function syncMobs(mobs, moveMs, siegeAngles) {
  const layer = el('map-mobs');
  const present = new Set(mobs.map(m => m.id));
  for (const [id, {node}] of mobNodes) {
    if (present.has(id)) continue;
    node.remove();
    mobNodes.delete(id);
  }

  const slots = new Map();
  const key = m => m.y * state.width + m.x;
  const sameCell = (m, prev) => prev && m.x === prev.x && m.y === prev.y;
  const slotsAt = m => {
    const cell = key(m);
    if (!slots.has(cell)) slots.set(cell, new Set());
    return slots.get(cell);
  };
  // Reserve the slots of troops that stay in place before a new troop can claim a slot.
  for (const m of mobs) {
    if (m.kind !== 'balaclava') continue;
    const prev = mobNodes.get(m.id);
    if (sameCell(m, prev)) slotsAt(m).add(prev.slot);
  }

  const dust = [];
  for (const m of mobs) {
    const prev = mobNodes.get(m.id);
    let [x, y] = center(m.x, m.y);
    let slot;
    if (m.kind === 'balaclava') {
      const used = slotsAt(m);
      slot = sameCell(m, prev) ? prev.slot : troopOffsets.findIndex((_, n) => !used.has(n));
      if (slot < 0) slot = 0;
      used.add(slot);
      const [dx, dy] = troopOffsets[slot];
      x += dx;
      y += dy;
    }
    const size = m.kind === 'giant' ? 35 : m.kind === 'apc' ? 31 : m.kind === 'balaclava' ? 18 : 25;
    const width = m.kind === 'balaclava' ? 13 : Math.round(size * .72);
    const node = prev?.node ?? document.createElementNS(layer.namespaceURI, 'g');
    if (!prev) {
      node.classList.add('battle-mob');
      node.dataset.id = m.id;
      node.innerHTML = `<g class="mob-body">${sprite(m.kind, 0, 0, size)}</g><g class="mob-flash"></g><g></g>`;
      layer.append(node);
    }
    const body = prev?.body ?? node.firstElementChild;
    if (!prev || prev.hp !== m.hp || prev.max !== m.max) {
      node.lastElementChild.innerHTML = healthIndicator(0, -size / 2 - 1.5, width, m.hp, m.max);
    }
    body.classList.toggle('frozen', Boolean(m.frozen));
    const angle = siegeAngles.get(m.id);
    const flash = body.nextElementSibling;
    if (angle !== undefined || flash.firstElementChild) {
      flash.innerHTML = angle === undefined ? '' : siegeMuzzle(angle);
    }
    if (!sameCell(m, prev)) {
      const transform = translateMob(x, y);
      node.style.transform = transform;
      const step = prev && Math.abs(m.x - prev.x) + Math.abs(m.y - prev.y) === 1;
      if (step && moveMs > 0 && !reducedBattleMotion.matches) {
        node.animate([{transform: translateMob(prev.px, prev.py)}, {transform}], {
          duration: moveMs, easing: 'cubic-bezier(.25,.1,.25,1)',
        });
        body.animate([
          {transform: 'scale(1)'},
          {transform: 'scale(1.08,.91)', offset: .18},
          {transform: 'scale(.96,1.05)', offset: .54},
          {transform: 'scale(1)'},
        ], moveMs);
        if (m.kind === 'apc') dust.push(apcDust(prev.px, prev.py, x - prev.px, y - prev.py));
      }
    }
    mobNodes.set(m.id, {node, body, x: m.x, y: m.y, px: x, py: y, slot, hp: m.hp, max: m.max});
  }
  el('map-dust').innerHTML = dust.join('');
}

function siegeDirection({cell, x, y}) {
  const [tx, ty] = point(cell);
  const [sx, sy] = center(x, y);
  return Math.atan2(ty - sy, tx - sx) * 180 / Math.PI;
}

const muzzleReach = 10;
function siegeMuzzle(angle) {
  return `<g transform="rotate(${angle}) translate(${muzzleReach} 0)">
    <path class="siege-muzzle" d="M-2 0 1-3 3-1 7 0 3 1 1 3Z"/>
  </g>`;
}

function siegeShot(target) {
  const [x, y] = center(target.x, target.y);
  const [tx, ty] = point(target.cell);
  const range = Math.hypot(tx - x, ty - y);
  const reach = Math.min(muzzleReach, range);
  const tail = Math.min(23, range - reach);
  const flight = range - reach - tail;
  return `<g class="siege-shot">
    <g transform="translate(${x} ${y}) rotate(${target.angle})">
      <path class="siege-streak" style="--siege-flight:${flight}px" d="M${reach} 0h${tail}"/>
      ${mobNodes.has(target.mobId) ? '' : siegeMuzzle(0)}
    </g>
    <circle class="siege" cx="${tx}" cy="${ty}" r="9"/>
  </g>`;
}

function drawBattle(frame, damage, previousBuildings, moveMs = 0) {
  const snapshot = replay?.snapshot ?? state;
  const buildings = frame?.buildings ?? snapshot.towers;
  const towerTypes = new Map(buildings.map(t => [t.cell, t.type]));
  const previousTypes = new Map(previousBuildings?.map(t => [t.cell, t.type]));
  const shots = frame?.shots || [];
  const targets = new Map([...(frame?.aims || []), ...shots].map(t => [t.cell, t]));
  const [kx, ky] = point(field.keep);
  const preview = !replay && planning() && cell !== undefined;
  // Only the smart slime has a guaranteed shortest route.
  const options = cell === undefined ? []
    : [route(cell), ...(field.alternates.get(cell) ?? [])].filter(Boolean);
  const shown = action === 'smart' ? options.slice(0, 1) : options;
  const routePreview = preview && !defending() && shown.length
    ? shown.map(path => polyline(path, path === options[0] ? 'route-preview' : 'route-preview alternate')).join('') : '';
  const fences = (frame?.fences ?? snapshot.fences).map(({cell: id, hp, max}) => {
    const [x, y] = point(id);
    return sprite('fence', x, y, 29) + healthIndicator(x, y + 14, 22, hp, max);
  }).join('');
  const ghost = preview && action ? `<g class="ghost">${defending() && action !== 'fence' ? towerGlyph(action, cell) : sprite(action, ...point(cell), 29)}</g>` : '';
  const hp = frame?.hp ?? snapshot.hp;
  const keep = `<g class="${damage ? 'keep-hit' : ''}">${sprite('keep', kx, ky, 36)}</g>`;
  const hits = damage ? `<text class="damage" x="${kx}" y="${Math.min(ky + 25, state.height * tile - 4)}">−${damage}</text>` : '';
  el('hp').textContent = hp;
  el('map-route').innerHTML = routePreview;
  el('map-ghost').innerHTML = ghost;
  el('map-keep').innerHTML = keep;
  el('map-fences').innerHTML = fences;
  syncTowers(buildings, targets);
  el('map-structure-hp').innerHTML = buildings.map(({cell: id, type, hp, max}) => {
    const [x, y] = point(id);
    return healthIndicator(x, y + (trap(type) ? 14 : -17), 22, hp, max);
  }).join('');
  const siege = (frame?.siege ?? []).map(t => ({...t, angle: siegeDirection(t)}));
  const siegeAngles = new Map(siege.map(t => [t.mobId, t.angle]));
  syncMobs(frame?.mobs ?? [], moveMs, siegeAngles);
  el('map-shots').innerHTML = shots.map(t => {
    // A shot can outlive its source in the same frame.
    const current = towerTypes.get(t.cell);
    return shot(t, current ?? previousTypes.get(t.cell), !current);
  }).join('');
  el('map-hits').innerHTML = hits + siege.map(siegeShot).join('');
}

let clock = null;
const remaining = () => clock ? Math.max(0, clock.left - Math.floor((performance.now() - clock.at) / 1000)) : 0;

function paintClock() {
  const node = el('clock');
  const show = Boolean(state && state.joined && !state.done && !state.locked && !replay);
  node.hidden = !show;
  if (!show) return;
  const left = remaining();
  node.textContent = left > 0 ? tr('timeLeft', { seconds: left }) : tr('timeUp');
  node.classList.toggle('urgent', left <= 5);
}

function render(redraw = true) {
  if (!state) return;
  const {gold, done, hp, joined, locked, last, orders, prices, towers} = state;
  const editable = planning();
  const skipping = !defending() && orders.length === 0;
  el('status').textContent = replay ? tr('battle', { wave: replay.snapshot.last, total: waves })
    : done ? tr('result', {
      outcome: message((hp > 0) === defending() ? 'win' : 'loss'),
      keep: message(hp > 0 ? 'keepStanding' : 'keepFell'),
    })
    : !joined ? tr('waitingPlayer')
    : locked ? tr('waitingReady')
    : tr('planning', { wave: last + 1, total: waves });
  el('rival').textContent = !joined ? tr('noOpponent')
    : state.opponent ? tr('opponent', { name: state.opponent }) : tr('guestOpponent');
  const round = replay?.snapshot.last ?? last;
  el('pips').innerHTML = Array.from({length: waves}, (_, i) => `<i class="${i < round ? 'done' : ''}${i === round && !done ? ' now' : ''}"></i>`).join('');
  paintClock();
  // A lone player may move to the other chair, so the button does not wait for the opponent
  const swapFree = !done && state.swap == null && last === 0 &&
    towers.length === 0 && state.fences.length === 0 && orders.length === 0 && !locked && !replay;
  el('swap').hidden = !swapFree;
  el('swapWait').hidden = !(state.swap === 'mine' && !replay);
  el('swapAsk').hidden = !(state.swap === 'theirs' && !replay);
  el('afterMatch').hidden = !(joined && done) || !!replay;
  el('field').hidden = false;
  el('planning').hidden = !joined || done || !!replay;
  el('budget').textContent = replay?.gold ?? gold;
  const occupied = new Set([...towers, ...state.fences].map(t => t.cell));
  const trapSites = new Set((state.trapCells ?? []).filter(id => !occupied.has(id)));
  const blocked = id => occupied.has(id) || (trap(action) && !trapSites.has(id));
  if (defending() && cell !== undefined && blocked(cell)) cell = undefined;
  for (const button of el('cells').children) {
    const id = Number(button.dataset.cell);
    button.hidden = defending() && (blocked(id) || onRoad(action) !== (button.dataset.kind === 'fence'));
    button.disabled = !editable || button.hidden;
    button.setAttribute('aria-pressed', String(editable && cell === id));
  }
  const used = new Map();
  for (const {type} of defending() ? towers : orders) used.set(type, (used.get(type) ?? 0) + 1);
  if (defending()) used.set('fence', state.fences.length);
  const maxFor = type => trap(type) ? undefined : state.limits?.[type];
  const canBuy = type => editable && gold >= prices[type] && (trap(type)
    ? trapSites.size > 0 : !(maxFor(type) > 0 && (used.get(type) ?? 0) >= maxFor(type)));
  for (const button of el('actions').children) {
    const type = button.dataset.action;
    const max = maxFor(type);
    button.querySelector('b').innerHTML = fare(prices[type]);
    button.querySelector('.action-limit').textContent = trap(type) ? tr('trapLimit')
      : max > 0 ? tr('limitUsage', { used: used.get(type) ?? 0, max }) : '';
    button.disabled = !canBuy(type);
    button.setAttribute('aria-pressed', String(action === type));
  }
  const routeName = action === 'smart' ? 'shortestRoute' : 'randomRoute';
  const [x, y] = cell === undefined ? [] : coords(cell).map(value => value + 1);
  const guidance = defending() ? (onRoad(action) ? 'chooseRoad' : 'chooseWall') : 'chooseGate';
  const hint = !editable ? '' : cell === undefined ? tr(guidance)
    : defending() ? tr('selectedCell', { x, y }) : tr('selectedGate', { gate: entry(cell), route: message(routeName) });
  el('hint').textContent = hint;
  el('hint').hidden = !hint;
  el('commit').innerHTML = action ? `${tr(defending() ? 'build' : 'addGroup')} · ${fare(prices[action])}`
    : tr(defending() ? 'chooseBuilding' : 'chooseGroup');
  el('commit').disabled = !action || cell === undefined || !canBuy(action);
  el('ready').textContent = tr(locked ? 'readyConfirmed' : skipping ? 'skipWave' : 'ready');
  el('ready').disabled = !editable;
  // Both sides list what they committed this turn: orders on the attack, structures on the defence
  const built = defending()
    ? [...towers.map(({type, cell: id}) => ({type, id})), ...state.fences.map(({cell: id}) => ({type: 'fence', id}))]
    : orders.map(({type, cell: id}, index) => ({type, id, index}));
  el('orders').hidden = built.length === 0;
  el('orders').replaceChildren(...built.map(row => {
    const li = document.createElement('li');
    const where = defending() ? coords(row.id).map(value => value + 1).join(', ') : entry(row.id);
    li.append(`${tr(`action.${row.type}`)} · ${where} `);
    const undo = document.createElement('button');
    undo.type = 'button';
    undo.textContent = tr('cancel');
    undo.disabled = !editable;
    undo.onclick = () => {
      if (undo.disabled) return;
      move(defending() ? 'unbuild' : 'cancel', defending() ? row.id : row.index);
    };
    li.append(undo);
    return li;
  }));
  if (redraw && !replay) drawBattle();
}

function play(previous) {
  if (replay || state.last <= seen || !state.frames.length) return;
  const snapshot = state;
  const batch = generation;
  const {frames} = snapshot;
  seen = snapshot.last;
  const {replayMs, replayLeftMs} = snapshot;
  if (!replayLeftMs) return;
  const end = performance.now() + replayLeftMs;
  const frameMs = replayMs / frames.length;
  replay = {snapshot, gold: previous?.gold ?? snapshot.gold};
  render();
  let index = -1, oldHp = previous?.last === seen - 1 ? previous.hp : frames[0].hp;
  let damage = 0, hitUntil = 0;
  const pulseCells = new Set(snapshot.towers.filter(t => t.type === 'pulse').map(t => t.cell));
  function step() {
    if (batch !== generation || replay?.snapshot !== snapshot) return;
    const timeLeft = end - performance.now();
    if (timeLeft <= 0) {
      replay = undefined;
      clearBattleActors();
      play(snapshot);
      if (!replay) {
        render();
        if (snapshot.done) sounds.play((snapshot.hp > 0) === defending() ? 'win' : 'loss');
      }
      return;
    }
    const nextIndex = Math.min(frames.length - 1, Math.floor((replayMs - timeLeft) / frameMs));
    const untilNext = Math.max(1, Math.ceil(Math.min(timeLeft, frameMs * (nextIndex + 1) - replayMs + timeLeft)));
    if (nextIndex === index) {
      setTimeout(step, untilNext);
      return;
    }
    // A missed frame has no visible start point for the next step.
    const moveMs = index >= 0 && nextIndex === index + 1 ? untilNext * .84 : 0;
    index = nextIndex;
    const frame = frames[index];
    const before = frames[index - 1];
    const previousBuildings = before?.buildings ?? (previous?.last === snapshot.last - 1 ? previous.towers : undefined);
    if (frame.mobs.length > (before?.mobs.length ?? 0)) sounds.play('slime', .34);
    if (frame.shots.some(s => pulseCells.has(s.cell))) sounds.play('rune', .23);
    if (frame.shots.some(s => !pulseCells.has(s.cell))) sounds.play('bolt', .16);
    if (before && frame.fences.length < before.fences.length) sounds.play('fence', .2);
    if (frame.hp < oldHp) {
      sounds.play('hit', .22);
      damage = oldHp - frame.hp;
      hitUntil = index + 4;
    } else if (index >= hitUntil) damage = 0;
    oldHp = frame.hp;
    el('map').style.setProperty('--battle-frame', `${untilNext}ms`);
    drawBattle(frame, damage, previousBuildings, moveMs);
    setTimeout(step, untilNext);
  }
  step();
}

function accept(next) {
  if (!Number.isInteger(next.width) || !Array.isArray(next.paths)) throw Object.assign(new Error('Missing duel board'), { feedback: message('missingBoard') });
  const previous = state;
  state = next;
  // The side decides what a click may build, so the field and its buttons are rebuilt together:
  // on the first state of a room and whenever the seat moves
  const rematched = previous && previous.matchId !== next.matchId;
  if (rematched) {
    replay = undefined;
    seen = 0;
    action = cell = undefined;
  }
  const rebuilt = !field || rematched || next.seat !== previous?.seat;
  if (rebuilt) {
    makeField();
    buildActions();
  }
  clock = {left: next.secondsLeft, at: performance.now()};
  if (next.last !== previous?.last || next.locked) action = cell = undefined;
  play(previous);
  render();
}

async function refresh() {
  const id = ++sequence;
  const next = await call(room('state'));
  if (id === sequence) accept(next);
}

function sync() {
  if (!roomId) return;
  refresh().catch(error => {
    // A seat can be taken away or a room can die; anything else is a passing hiccup
    if ([401, 403, 404].includes(error.status)) leave(feedback(error));
    else fail(error);
  });
}

async function move(type, target) {
  if (sending || !state) return;
  const wave = state.last + 1;
  sending = true;
  ++sequence;
  errorNote = null;
  paintError();
  render();
  try {
    const next = await call(room('moves'), {
      method: 'POST',
      body: {wave, action: type, cell: target === undefined ? null : target},
    });
    if (type !== 'ready' && type !== 'rematch') {
      if (defending()) action = cell = undefined;
      sounds.play('coin');
    }
    accept(next);
  } catch (error) {
    fail(error);
  } finally {
    sending = false;
    render();
    sync();
  }
}

async function swap(command) {
  if (sending || !state) return;
  sending = true;
  ++sequence;
  errorNote = null;
  paintError();
  render();
  try {
    accept(await call(room(command), {method: 'POST'}));
  } catch (error) {
    fail(error);
  } finally {
    sending = false;
    render();
    sync();
  }
}

// The side can change mid-room, so the order buttons are rebuilt whenever the seat moves
function buildActions() {
  el('actions').replaceChildren();
  action = cell = undefined;
  for (const type of types().filter(type => type in state.prices)) {
    const button = document.createElement('button');
    button.dataset.action = type;
    button.innerHTML = `<svg aria-hidden="true" viewBox="0 0 32 32">${sprite(type, 16, 16, 32)}</svg><strong></strong> <b></b><span><span class="action-hint"></span><small class="action-limit"></small></span>`;
    button.onclick = () => {
      if (button.disabled) return;
      if (defending() && onRoad(action) !== onRoad(type)) cell = undefined;
      action = type;
      render();
    };
    button.disabled = true;
    el('actions').append(button);
  }
  paintActions();
}

function paintActions() {
  el('role').textContent = tr(defending() ? 'defender' : 'attacker');
  for (const button of el('actions').children) {
    const type = button.dataset.action;
    button.querySelector('strong').textContent = tr(`action.${type}`);
    button.querySelector('.action-hint').textContent = tr(`hint.${type}`);
  }
}

function paintRoomControls() {
  el('leave').textContent = tr(Tower.token ? 'backToDuels' : 'backHome');
  el('copy').textContent = tr(copyNote);
}

function paintInvite() {
  el('link').value = Tower.linkFor(roomId);
}

// The board wipes its own screen so a stale maze never shows over a new room
function wipe() {
  clearBattleActors();
  clearBattleActors();
  state = field = action = cell = replay = undefined;
  seen = 0;
  clock = null;
  clearTimeout(copyTimer);
  copyNote = 'copyLink';
  el('map').innerHTML = '';
  el('cells').replaceChildren();
  el('actions').replaceChildren();
  el('orders').replaceChildren();
  el('orders').hidden = true;
  el('pips').innerHTML = '';
  el('status').textContent = '';
  el('rival').textContent = '';
  el('role').textContent = '';
  el('clock').hidden = true;
  el('field').hidden = true;
  el('planning').hidden = true;
  el('swap').hidden = true;
  el('swapWait').hidden = true;
  el('swapAsk').hidden = true;
  el('afterMatch').hidden = true;
  el('invite').open = false;
}

async function open(detail = {}) {
  const id = String(detail.roomId ?? '').trim();
  if (!id) return;
  const batch = ++generation;
  ++sequence;
  wipe();
  sending = false;
  roomId = id;
  seatToken = detail.seatToken ?? Tower.seatOf(id) ?? null;
  errorNote = null;
  paintError();
  paintRoomControls();
  el('status').textContent = tr('loading');
  el('game').hidden = false;
  el('invite').open = false;
  paintInvite();
  history.replaceState(null, '', `?room=${encodeURIComponent(id)}`);
  try {
    await refresh();
  } catch (error) {
    // The lobby owns the message once the board is gone
    if (batch === generation) leave(feedback(error));
    return;
  }
  if (batch !== generation) return;
  el('invite').open = !state.joined;
  Tower.watchRoom(id, seatToken).catch(() => {});
  if (!timer) timer = setInterval(tick, 2000);
  // The clock counts whole seconds, so it repaints between the polls, not twice a second
  if (!clockTimer) clockTimer = setInterval(() => { if (roomId && !replay) paintClock(); }, 250);
}

function tick() {
  if (!roomId) return;
  if (remaining() === 0 && !state?.done && state?.joined && !replay) render();
  else paintClock();
  // The socket carries every change; the poll is the fallback when it never came up.
  // While it is up, a rare reconcile keeps a broadcast that went missing from
  // freezing the board — a silent stale maze is worse than one spare request
  if (!Tower.connected()) {
    quiet = 0;
    sync();
  } else if (++quiet >= 8) {
    quiet = 0;
    sync();
  }
}

function leave(message) {
  if (!roomId) return;
  ++generation;
  ++sequence;
  if (timer) clearInterval(timer);
  timer = null;
  roomId = null;
  seatToken = null;
  sending = false;
  wipe();
  el('game').hidden = true;
  errorNote = null;
  paintError();
  el('link').value = '';
  history.replaceState(null, '', location.pathname);
  window.dispatchEvent(new CustomEvent('tower:screen', { detail: {screen: 'lobby', message: message || null} }));
}

el('commit').onclick = () => { if (!el('commit').disabled) move(action, cell); };
el('ready').onclick = () => { if (!el('ready').disabled) move('ready', null); };
el('rematch').onclick = () => { if (!el('afterMatch').hidden) move('rematch', null); };
el('swap').onclick = () => { if (!el('swap').hidden) swap('swap'); };
el('swapYes').onclick = () => swap('swap/accept');
el('swapNo').onclick = () => swap('swap/decline');
el('leave').onclick = () => leave(null);
el('again').addEventListener('click', () => leave(null));
el('copy').onclick = async () => {
  const batch = generation;
  let result;
  try {
    await navigator.clipboard.writeText(el('link').value);
    result = 'linkCopied';
  } catch { result = 'selectLink'; }
  if (batch !== generation) return;
  if (result === 'selectLink') el('link').select();
  copyNote = result;
  paintRoomControls();
  clearTimeout(copyTimer);
  copyTimer = setTimeout(() => {
    copyNote = 'copyLink';
    paintRoomControls();
  }, 2400);
};

i18n.onChange(() => {
  if (!roomId) return;
  paintRoomControls();
  paintError();
  if (!state) {
    el('status').textContent = tr('loading');
    return;
  }
  paintCells();
  paintActions();
  render(false);
});

Tower.on('Changed', () => { if (roomId && !sending) sync(); });
// Joining the room is not instant: whatever the opponent did meanwhile was broadcast
// before we were in the group, so the board catches up right after the join
Tower.on('RoomJoined', () => { if (roomId && !sending) sync(); });

window.addEventListener('tower:screen', event => {
  const detail = event.detail ?? {};
  if (detail.screen === 'game') open(detail);
});
