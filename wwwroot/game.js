const el = id => document.getElementById(id);
const waves = 5;
const tile = 40;
const tickMs = 90;
const ticketKey = id => `duel:${id}`;
const currentKey = 'duel:current';
const choices = {
  bolt: ['Арбалет', 'Сильный удар по одной цели'],
  pulse: ['Руна', 'Бьёт всех в радиусе'],
  swarm: ['Стайка', 'Пять мелких слизней'],
  giant: ['Гигант', 'Один живучий слизень'],
  smart: ['Умные слизни', 'Пять в очках идут кратчайшим путём'],
  runner: ['Бегуны', 'Шесть быстрых, но хрупких'],
  armored: ['Бронированные', 'Три медленных, держат удар'],
  decoy: ['Приманка', 'Четыре слизня пойдут длинным путём'],
  ranged: ['Дальнобойные', 'Два слизня с винтовками бьют по постройкам'],
  apc: ['Бронетранспортёр', 'На середине пути высадит четырёх в балаклавах'],
  fence: ['Забор', 'Слизни остановятся, чтобы его сломать'],
  cannon: ['Пушка', 'Медленно бьёт, зато больно'],
  frost: ['Мороз', 'Замедляет всех в радиусе'],
  spikes: ['Шипы', 'Ловушка на тропе, колет наступившего'],
};
let player, state, field, action, cell, replay, room;
let hubConnection;
let generation = 0;
let seen = 0, sequence = 0, sending = false;
const onRoad = type => type === 'fence' || type === 'spikes';
const types = () => player.seat === 0
  ? ['bolt', 'pulse', 'cannon', 'frost', 'spikes', 'fence']
  : ['swarm', 'giant', 'smart', 'runner', 'armored', 'decoy', 'ranged', 'apc'];
const defending = () => player.seat === 0;
const planning = () => state.joined && !state.done && !state.locked && !replay && !sending;
const route = id => field.routes.get(id);
const entry = id => `Вход ${field.entries.indexOf(id) + 1}`;
const coords = id => [id % state.width, Math.floor(id / state.width)];
const center = (x, y) => [(x + .5) * tile, (y + .5) * tile];
const point = id => center(...coords(id));
const sprite = (id, x, y, size) => `<use href="actors.svg#${id}" x="${x - size / 2}" y="${y - size / 2}" width="${size}" height="${size}"/>`;
const fare = cost => `<span class="gold">${Number(cost)} <svg class="coin" aria-hidden="true" viewBox="0 0 32 32">${sprite('coin', 16, 16, 32)}</svg><span class="gold-unit"> зол.</span></span>`;
const polyline = (path, cls) => `<polyline class="${cls}" points="${path.map(id => point(id).join(',')).join(' ')}"/>`;

async function post(path, data) {
  const response = await fetch(path, {
    method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(data),
  });
  if (!response.ok) {
    const error = Error(({400: 'Этот приказ недоступен', 401: 'Ссылка входа устарела',
      404: 'Комната не найдена', 409: 'Место занято или условия хода изменились'})[response.status] ||
      `Запрос не прошёл: ${response.status}`);
    error.status = response.status;
    throw error;
  }
  return response.json();
}
const endpoint = path => `/room/${encodeURIComponent(player.id)}/${path}`;
const fail = error => { el('error').textContent = error.message; };

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
  el('map').innerHTML = `<g id="map-road">${road}</g><g id="map-markers">${markers}</g>` +
    `<g id="map-route"></g><g id="map-keep"></g><g id="map-ghost"></g><g id="map-fences"></g>` +
    `<g id="map-towers"></g><g id="map-mobs"></g><g id="map-shots"></g><g id="map-hits"></g>`;
  for (const id of defending() ? [...walls, ...fenceCells] : entries) {
    const button = document.createElement('button');
    const [x, y] = coords(id);
    button.className = 'cell';
    button.dataset.cell = id;
    const fenceSite = defending() && pathsSet.has(id);
    button.dataset.kind = defending() ? (fenceSite ? 'fence' : 'wall') : 'entry';
    button.style.gridArea = `${y + 1} / ${x + 1}`;
    button.setAttribute('aria-label', defending() ? `${fenceSite ? 'Тропа' : 'Стена'}: столбец ${x + 1}, ряд ${y + 1}` : entry(id));
    button.onclick = () => { cell = cell === id ? undefined : id; render(); };
    el('cells').append(button);
  }
}

function aimAngle(x, y, target) {
  const [tx, ty] = center(target.x, target.y);
  return Math.atan2(ty - y, tx - x) * 180 / Math.PI;
}

const turning = type => type === 'bolt' || type === 'cannon';

const towerBody = type => type === 'bolt'
  ? `<circle class="bolt-base" r="12"/><path class="bolt-bow" d="M-10 0H12M3-10Q13 0 3 10"/><path class="bolt-string" d="M3-10L-7 0 3 10"/>`
  : `<circle class="cannon-base" r="12"/><path class="cannon-barrel" d="M-7-3.4h10l5 3.4-5 3.4H-7z"/><path class="cannon-muzzle" d="M8-4.4v8.8"/>`;

const aimAt = (id, target) => target ? aimAngle(...point(id), target) : -40;
const transformOf = (id, target) => `translate(${point(id)[0]}px, ${point(id)[1]}px) rotate(${aimAt(id, target)}deg)`;

// Towers sit in their own layer: a new frame changes one transform, so CSS can ease the turn
function syncTowers(towers, targets) {
  const layer = el('map-towers');
  const signature = towers.map(t => `${t.cell}:${t.type}`).join('|');
  if (layer.dataset.signature !== signature) {
    layer.dataset.signature = signature;
    layer.innerHTML = towers.map(({type, cell: id}) => turning(type)
      ? `<g class="tower" data-cell="${id}" style="transform: ${transformOf(id, targets.get(id))}">${towerBody(type)}</g>`
      : sprite(type, ...point(id), 31)).join('');
    return;
  }
  for (const node of layer.querySelectorAll('g.tower')) {
    const id = Number(node.dataset.cell);
    node.style.transform = transformOf(id, targets.get(id));
  }
}

function shot(target, type) {
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
    return `<g class="spike-shot"><circle cx="${tx}" cy="${ty}" r="9"/></g>`;
  }
  if (type === 'cannon') {
    return `<g transform="translate(${x} ${y}) rotate(${aimAngle(x, y, target)})"><circle class="projectile cannonball" ${fly} r="4"/></g>`;
  }
  return `<g transform="translate(${x} ${y}) rotate(${aimAngle(x, y, target)})">
    <path class="projectile" ${fly} d="M-11 0H0M-4-3L0 0-4 3"/>
  </g>`;
}

function drawBattle(frame, damage) {
  const buildings = frame?.buildings ?? (replay?.snapshot || state).towers;
  const towerTypes = new Map(buildings.map(t => [t.cell, t.type]));
  const shots = frame?.shots || [];
  const targets = new Map([...(frame?.aims || []), ...shots].map(t => [t.cell, t]));
  const [kx, ky] = point(field.keep);
  const preview = !replay && planning() && cell !== undefined;
  // The pawn shows every way it may go; the smart slime walks the short way, the decoy the long one
  const options = cell === undefined ? []
    : [route(cell), ...(field.alternates.get(cell) ?? [])].filter(Boolean);
  const longest = options.length ? options.reduce((best, path) => path.length > best.length ? path : best) : null;
  const shown = action === 'smart' ? options.slice(0, 1) : action === 'decoy' && longest ? [longest] : options;
  const routePreview = preview && !defending() && shown.length
    ? shown.map(path => polyline(path, path === options[0] ? 'route-preview' : 'route-preview alternate')).join('') : '';
  const fences = (frame?.fences ?? state.fences).map(({cell: id, hp}) => {
    const [x, y] = point(id);
    return `<g class="fence">${sprite('fence', x, y, 29)}<text x="${x + 9}" y="${y - 9}">${hp}</text></g>`;
  }).join('');
  const ghost = preview && action ? `<g class="ghost">${sprite(action, ...point(cell), 31)}</g>` : '';
  const keep = `<g class="${damage ? 'keep-hit' : ''}">${sprite('keep', kx, ky, 44)}</g>`;
  const hits = damage ? `<text class="damage" x="${Math.max(18, Math.min(kx, state.width * tile - 18))}" y="${Math.max(15, ky - 14)}">−${damage}</text>` : '';
  el('hp').textContent = frame?.hp ?? state.hp;
  el('map-route').innerHTML = routePreview;
  el('map-ghost').innerHTML = ghost;
  el('map-keep').innerHTML = keep;
  el('map-fences').innerHTML = fences;
  syncTowers(buildings, targets);
  el('map-mobs').innerHTML = (frame?.mobs || []).map(m => {
    const glyph = sprite(m.kind ?? 'swarm', ...center(m.x, m.y), m.kind === 'giant' ? 35 : m.kind === 'apc' ? 31 : 25);
    return m.frozen ? `<g class="frozen">${glyph}</g>` : glyph;
  }).join('');
  el('map-shots').innerHTML = shots.map(t => shot(t, towerTypes.get(t.cell))).join('');
  el('map-hits').innerHTML = hits + (frame?.siege || []).map(({cell: id}) => {
    const [sx, sy] = point(id);
    return `<circle class="siege" cx="${sx}" cy="${sy}" r="13"/>`;
  }).join('');
}

let clock = null;
const remaining = () => clock ? Math.max(0, clock.left - Math.floor((Date.now() - clock.at) / 1000)) : 0;

function paintClock() {
  const node = el('clock');
  const show = state && state.joined && !state.done && !state.locked && !replay;
  node.hidden = !show;
  if (!show) return;
  const left = remaining();
  node.textContent = left > 0 ? `на размышление ${left} с` : 'время вышло';
  node.classList.toggle('urgent', left <= 5);
}

function render() {
  if (!state) return;
  const {gold, done, hp, joined, locked, last, orders, prices, towers} = state;
  const editable = planning();
  const waitingForOrders = !defending() && orders.length === 0;
  el('status').textContent = replay ? `Волна ${replay.snapshot.last} из ${waves} · бой`
    : done ? `${(hp > 0) === defending() ? 'Победа' : 'Поражение'} · ${hp > 0 ? 'крепость выстояла' : 'крепость пала'}`
    : !joined ? 'Ждём второго игрока'
    : locked ? 'Ты готов. Ждём соперника'
    : `Волна ${last + 1} из ${waves} · подготовка`;
  const round = replay?.snapshot.last ?? last;
  el('pips').innerHTML = Array.from({length: waves}, (_, i) => `<i class="${i < round ? 'done' : ''}${i === round && !done ? ' now' : ''}"></i>`).join('');
  paintClock();
  el('swap').hidden = !(joined && !done && last === 0 && !locked && towers.length === 0 && state.fences.length === 0);
  el('rematch').hidden = !(joined && done);
  el('field').hidden = false;
  el('again').hidden = !done || !!replay;
  el('planning').hidden = !joined || done || !!replay;
  el('budget').textContent = replay?.gold ?? gold;
  const occupied = new Set([...towers, ...state.fences].map(t => t.cell));
  if (defending() && occupied.has(cell)) cell = undefined;
  for (const button of el('cells').children) {
    const id = Number(button.dataset.cell);
    button.hidden = defending() && (occupied.has(id) || onRoad(action) !== (button.dataset.kind === 'fence'));
    button.disabled = !editable || button.hidden;
    button.setAttribute('aria-pressed', String(editable && cell === id));
  }
  for (const button of el('actions').children) {
    const type = button.dataset.action;
    button.querySelector('b').innerHTML = fare(prices[type]);
    button.disabled = !editable || gold < prices[type];
    button.setAttribute('aria-pressed', String(action === type));
  }
  el('hint').textContent = !editable ? '' : cell === undefined
    ? (defending() ? (onRoad(action) ? 'Выбери отмеченную клетку на тропе' : 'Выбери стену у тропы или перейди на забор и шипы') : 'Выбери вход, чтобы увидеть маршруты')
    : (defending() ? `Выбрана клетка: ${coords(cell).map(value => value + 1).join(', ')}` : `${entry(cell)} · ${action === 'smart' ? 'кратчайший путь' : action === 'decoy' ? 'длинный путь' : 'выбор ветки 50/50'}`);
  el('hint').hidden = !el('hint').textContent;
  el('commit').innerHTML = action ? `${defending() ? 'Построить' : 'Добавить в волну'} · ${fare(prices[action])}`
    : (defending() ? 'Выбери постройку' : 'Выбери группу');
  el('commit').disabled = !editable || !action || cell === undefined || gold < prices[action];
  el('ready').textContent = locked ? 'Готовность подтверждена' : waitingForOrders ? 'Добавь группу, чтобы начать' : 'Готов к волне';
  el('ready').disabled = !editable || waitingForOrders;
  el('orders').hidden = orders.length === 0;
  el('orders').replaceChildren(...orders.map(({type, cell: id}, index) => {
    const li = document.createElement('li');
    li.append(`${choices[type][0]} · ${entry(id)} `);
    const undo = document.createElement('button');
    undo.type = 'button';
    undo.textContent = 'Отменить';
    undo.disabled = !editable;
    undo.onclick = () => { if (!undo.disabled) move('cancel', index); };
    li.append(undo);
    return li;
  }));
  if (!replay) drawBattle();
}

function play(previous) {
  if (replay || state.last <= seen || !state.frames.length) return;
  const snapshot = state;
  const batch = generation;
  const {frames} = snapshot;
  seen = snapshot.last;
  replay = {snapshot, gold: previous?.gold ?? snapshot.gold};
  render();
  let index = 0, oldHp = previous?.last === seen - 1 ? previous.hp : frames[0].hp;
  let damage = 0, hitUntil = 0;
  const pulseCells = new Set(snapshot.towers.filter(t => t.type === 'pulse').map(t => t.cell));
  function step() {
    if (batch !== generation) return;
    const frame = frames[index];
    const before = frames[index - 1];
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
    drawBattle(frame, damage);
    if (++index < frames.length) setTimeout(step, tickMs);
    else setTimeout(() => {
      if (batch !== generation) return;
      replay = undefined;
      play(snapshot);
      if (!replay) {
        render();
        if (snapshot.done) sounds.play((snapshot.hp > 0) === defending() ? 'win' : 'loss');
      }
    }, 460);
  }
  step();
}

function accept(next) {
  if (!Number.isInteger(next.width) || !Array.isArray(next.paths)) throw Error('Сервер не прислал поле дуэли');
  const previous = state;
  state = next;
  if (Number.isInteger(next.seat) && next.seat !== player?.seat) {
    player.seat = next.seat;
    sessionStorage.setItem(`duel:${player.id}`, JSON.stringify(player));
    buildActions();
  }
  clock = {left: Number.isFinite(next.secondsLeft) ? next.secondsLeft : 0, at: Date.now()};
  if (!field) makeField();
  if (next.last !== previous?.last || next.locked) action = cell = undefined;
  if (next.done) store.set(currentKey, null);
  play(previous);
  render();
}

async function refresh() {
  if (sending) return;
  const id = ++sequence;
  const next = await post(endpoint('state'), {key: player.key});
  if (id === sequence) accept(next);
}
const sync = () => refresh().catch(fail);

async function move(type, target) {
  const wave = state.last + 1;
  sending = true;
  ++sequence;
  el('error').textContent = '';
  render();
  try {
    const next = await post(endpoint('move'), {key: player.key, wave, action: type, cell: target});
    if (type !== 'ready') {
      if (defending()) action = cell = undefined;
      sounds.play('coin');
    }
    accept(next);
  } catch (error) {
    fail(error);
  } finally {
    sending = false;
    render();
    await sync();
  }
}

// The side can change mid-room, so the order buttons are rebuilt whenever the seat moves
function buildActions() {
  el('role').textContent = defending() ? 'ЗАЩИТНИК' : 'АТАКУЮЩИЙ';
  el('actions').replaceChildren();
  action = cell = undefined;
  for (const type of types()) {
    const [label, hint] = choices[type];
    const button = document.createElement('button');
    button.dataset.action = type;
    button.innerHTML = `<svg aria-hidden="true" viewBox="0 0 32 32">${sprite(type, 16, 16, 32)}</svg>${label} <b></b><span>${hint}</span>`;
    button.onclick = () => {
      if (defending() && onRoad(action) !== onRoad(type)) cell = undefined;
      action = type;
      render();
    };
    button.disabled = true;
    el('actions').append(button);
  }
}

async function alive(player, allowDone = false) {
  try {
    const next = await post(`/room/${encodeURIComponent(player.id)}/state`, {key: player.key});
    return allowDone || !next.done;
  } catch (error) {
    return error.status !== 401 && error.status !== 404;
  }
}

// A ticket lives in the browser itself: no Telegram, no server-side session
const store = {
  async get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  async set(key, value) {
    try { value === null ? localStorage.removeItem(key) : localStorage.setItem(key, value); } catch {}
  },
};

async function paintInvite() {
  const web = new URL(location.origin + location.pathname);
  web.search = new URLSearchParams({room, join: '1'});
  el('link').value = web.href;
}

async function enter(saved) {
  const id = ++generation;
  el('start').disabled = true;
  el('error').textContent = '';
  try {
    let stored = saved;
    const joining = Boolean(room);
    if (!stored && room) {
      try { stored = JSON.parse(await store.get(ticketKey(room))); } catch {}
    }
    player = stored ?? await post(joining ? `/room/${encodeURIComponent(room)}/join` : '/room', {});
    const fresh = !stored && !joining;
    room = player.id;
    await store.set(ticketKey(room), JSON.stringify(player));
    await store.set(currentKey, room);
    const url = new URL(location.href);
    url.search = new URLSearchParams({room});
    history.replaceState(null, '', url);
    buildActions();
    el('game').hidden = false;
    el('cover').hidden = true;
    el('status').textContent = 'Загружаем поле';
    try {
      await refresh();
    } catch (error) {
      el('game').hidden = true;
      el('cover').hidden = false;
      throw error;
    }
    el('invite').open = fresh;
    await paintInvite();
    const hub = globalThis.signalR && new signalR.HubConnectionBuilder()
      .withUrl('/bell').withAutomaticReconnect().build();
    let watching = false;
    setInterval(() => { if (id === generation && (!watching || hub?.state !== 'Connected' || !state)) sync(); }, 2000);
    if (!hub) return true;
    hubConnection = hub;
    hub.on('changed', sync);
    const watch = async () => {
      watching = false;
      await hub.invoke('Watch', player.id);
      watching = true;
      await sync();
    };
    hub.onreconnected(() => watch().catch(fail));
    await hub.start();
    await watch();
    return true;
  } catch (error) {
    fail(error);
    el('start').disabled = false;
    return false;
  }
}

function restart() {
  ++generation;
  ++sequence;
  store.set(currentKey, null);
  hubConnection?.stop().catch(() => {});
  hubConnection = undefined;
  player = state = field = action = cell = replay = undefined;
  seen = 0;
  sending = false;
  clock = null;
  room = null;
  el('error').textContent = '';
  el('start').disabled = false;
  el('start').textContent = 'Создать дуэль';
  el('again').hidden = true;
  el('game').hidden = true;
  el('cover').hidden = false;
  el('actions').replaceChildren();
  el('orders').replaceChildren();
  el('cells').replaceChildren();
  el('map').innerHTML = '';
  el('status').textContent = '';
  el('clock').hidden = true;
  const url = new URL(location.href);
  url.search = '';
  history.replaceState(null, '', url);
}

el('swap').onclick = () => { if (!el('swap').hidden) move('swap', 0); };
el('rematch').onclick = () => { if (!el('rematch').hidden) move('rematch', 0); };

el('commit').onclick = () => { if (!el('commit').disabled) move(action, cell); };
el('ready').onclick = () => { if (!el('ready').disabled) move('ready', 0); };
el('copy').onclick = async () => {
  let copied = false;
  try {
    await navigator.clipboard.writeText(el('link').value);
    copied = true;
  } catch { el('link').select(); }
  el('copy').textContent = copied ? 'Ссылка скопирована' : 'Выделите ссылку';
  setTimeout(() => { el('copy').textContent = 'Скопировать ссылку'; }, 2400);
};
el('start').onclick = () => enter();
el('again').addEventListener('click', event => { event.preventDefault(); restart(); });

async function boot() {
  const query = new URLSearchParams(location.search);
  room = query.get('room');
  if (room) {
    // An invite link opens the ticket this browser already holds for that room
    try {
      const stored = JSON.parse(await store.get(ticketKey(room)));
      if (stored && await alive(stored, true)) {
        if (await enter(stored)) return;
        el('start').textContent = 'Продолжить дуэль';
        return;
      }
    } catch {}
    el('start').textContent = 'Вступить в дуэль';
    return;
  }
  const current = await store.get(currentKey);
  if (!current) return;
  try {
    const stored = JSON.parse(await store.get(ticketKey(current)));
    if (stored && await alive(stored)) {
      room = current;
      if (await enter(stored)) return;
      el('start').textContent = 'Продолжить дуэль';
    }
  } catch {}
}
boot();
