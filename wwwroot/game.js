const el = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const room = params.get('room') || params.get('tgWebAppStartParam');
const waves = 5;
const tile = 40;
const tickMs = 90;
const choices = {
  bolt: ['Арбалет', 'Сильный удар по одной цели'],
  pulse: ['Руна', 'Бьёт всех в радиусе'],
  swarm: ['Стайка', 'Пять мелких слизней'],
  giant: ['Гигант', 'Один живучий слизень'],
  smart: ['Умные слизни', 'Пять слизней в очках идут кратчайшим путём'],
  fence: ['Забор', 'Слизни остановятся, чтобы его сломать'],
};
let player, state, field, action, cell, replay;
let seen = 0, sequence = 0, sending = false;
const types = () => player.seat === 0 ? ['bolt', 'pulse', 'fence'] : ['swarm', 'giant', 'smart'];
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
  if (!response.ok) throw Error(({400: 'Этот приказ недоступен', 401: 'Ссылка входа устарела',
    404: 'Комната не найдена', 409: 'Место занято или условия хода изменились'})[response.status] || `Запрос не прошёл: ${response.status}`);
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

function tower({type, cell: id}, target) {
  const [x, y] = point(id);
  if (type !== 'bolt') return sprite('pulse', x, y, 31);
  return `<g transform="translate(${x} ${y})">
    <circle class="bolt-base" r="12"/>
    <g class="bolt-aim" transform="rotate(${target ? aimAngle(x, y, target) : -40})">
      <path class="bolt-bow" d="M-10 0H12M3-10Q13 0 3 10"/>
      <path class="bolt-string" d="M3-10L-7 0 3 10"/>
    </g></g>`;
}

function shot(target, type) {
  const [x, y] = point(target.cell);
  const [tx, ty] = center(target.x, target.y);
  if (type === 'pulse') {
    return `<g class="pulse-shot"><path d="M${x},${y}L${tx},${ty}"/><circle cx="${tx}" cy="${ty}" r="10"/></g>`;
  }
  const distance = Math.hypot(tx - x, ty - y);
  return `<g transform="translate(${x} ${y}) rotate(${aimAngle(x, y, target)})">
    <path class="projectile" style="--flight:${distance}px" d="M-11 0H0M-4-3L0 0-4 3"/>
  </g>`;
}

function drawBattle(frame, damage) {
  const towers = (replay?.snapshot || state).towers;
  const towerTypes = new Map(towers.map(t => [t.cell, t.type]));
  const shots = frame?.shots || [];
  const targets = new Map([...(frame?.aims || []), ...shots].map(t => [t.cell, t]));
  const [kx, ky] = point(field.keep);
  const preview = !replay && planning() && cell !== undefined;
  const routePreview = preview && !defending()
    ? polyline(route(cell), 'route-preview') + (action === 'smart' ? '' : field.alternates.get(cell).map(p => polyline(p, 'route-preview alternate')).join('')) : '';
  const fences = (frame?.fences ?? state.fences).map(({cell: id, hp}) => {
    const [x, y] = point(id);
    return `<g class="fence">${sprite('fence', x, y, 29)}<text x="${x + 9}" y="${y - 9}">${hp}</text></g>`;
  }).join('');
  const ghost = preview && action ? `<g class="ghost">${sprite(action, ...point(cell), 31)}</g>` : '';
  const keep = `<g class="${damage ? 'keep-hit' : ''}">${sprite('keep', kx, ky, 44)}</g>`;
  const hits = damage ? `<text class="damage" x="${Math.max(18, Math.min(kx, state.width * tile - 18))}" y="${Math.max(15, ky - 14)}">−${damage}</text>` : '';
  el('hp').textContent = frame?.hp ?? state.hp;
  el('map').innerHTML = field.road + routePreview + field.markers + keep + ghost +
    towers.map(t => tower(t, targets.get(t.cell))).join('') + fences +
    (frame?.mobs || []).map(m => sprite(m.giant ? 'giant' : m.smart ? 'smart' : 'swarm', ...center(m.x, m.y), m.giant ? 35 : 25)).join('') +
    shots.map(t => shot(t, towerTypes.get(t.cell))).join('') + hits;
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
  el('field').hidden = false;
  el('again').hidden = !done || !!replay;
  el('planning').hidden = !joined || done || !!replay;
  el('budget').textContent = replay?.gold ?? gold;
  const occupied = new Set([...towers, ...state.fences].map(t => t.cell));
  if (defending() && occupied.has(cell)) cell = undefined;
  for (const button of el('cells').children) {
    const id = Number(button.dataset.cell);
    button.hidden = defending() && (occupied.has(id) || (action === 'fence') !== (button.dataset.kind === 'fence')); 
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
    ? (defending() ? (action === 'fence' ? 'Выбери отмеченную клетку на тропе' : 'Выбери стену у тропы или переключись на забор') : 'Выбери вход, чтобы увидеть маршруты')
    : (defending() ? `Выбрана клетка: ${coords(cell).map(value => value + 1).join(', ')}` : `${entry(cell)} · ${action === 'smart' ? 'кратчайший путь' : 'выбор ветки 50/50'}`);
  el('hint').hidden = !el('hint').textContent;
  el('commit').innerHTML = action ? `${defending() ? 'Построить' : 'Добавить в волну'} · ${fare(prices[action])}`
    : (defending() ? 'Выбери постройку' : 'Выбери группу');
  el('commit').disabled = !editable || !action || cell === undefined || gold < prices[action];
  el('ready').textContent = locked ? 'Готовность подтверждена' : waitingForOrders ? 'Добавь группу, чтобы начать' : 'Готов к волне';
  el('ready').disabled = !editable || waitingForOrders;
  el('orders').hidden = orders.length === 0;
  el('orders').replaceChildren(...orders.map(({type, cell: id}) => {
    const li = document.createElement('li');
    li.textContent = `${choices[type][0]} · ${entry(id)}`;
    return li;
  }));
  if (!replay) drawBattle();
}

function play(previous) {
  if (replay || state.last <= seen || !state.frames.length) return;
  const snapshot = state;
  const {frames} = snapshot;
  seen = snapshot.last;
  replay = {snapshot, gold: previous?.gold ?? snapshot.gold};
  render();
  let index = 0, oldHp = previous?.last === seen - 1 ? previous.hp : frames[0].hp;
  let damage = 0, hitUntil = 0;
  const pulseCells = new Set(snapshot.towers.filter(t => t.type === 'pulse').map(t => t.cell));
  function step() {
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
  if (!field) makeField();
  if (next.last !== previous?.last || next.locked) action = cell = undefined;
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

async function enter() {
  el('start').disabled = true;
  el('error').textContent = '';
  try {
    const stored = room && !params.has('join') && sessionStorage.getItem(`duel:${room}`);
    player = stored ? JSON.parse(stored) : await post(room ? `/room/${encodeURIComponent(room)}/join` : '/room', {});
    sessionStorage.setItem(`duel:${player.id}`, JSON.stringify(player));
    const url = new URL(location.href);
    url.search = new URLSearchParams({room: player.id});
    history.replaceState(null, '', url);
    url.searchParams.set('join', '1');
    el('link').value = url.href;
    el('invite').open = !room;
    el('role').textContent = defending() ? 'ЗАЩИТНИК' : 'АТАКУЮЩИЙ';
    for (const type of types()) {
      const [label, hint] = choices[type];
      const button = document.createElement('button');
      button.dataset.action = type;
      button.innerHTML = `<svg aria-hidden="true" viewBox="0 0 32 32">${sprite(type, 16, 16, 32)}</svg>${label} <b></b><span>${hint}</span>`;
      button.onclick = () => {
        if (defending() && (action === 'fence') !== (type === 'fence')) cell = undefined;
        action = type;
        render();
      };
      button.disabled = true;
      el('actions').append(button);
    }
    el('game').hidden = false;
    el('cover').hidden = true;
    el('status').textContent = 'Загружаем поле';
    await sync();
    const hub = globalThis.signalR && new signalR.HubConnectionBuilder()
      .withUrl('/bell').withAutomaticReconnect().build();
    let watching = false;
    setInterval(() => { if (!watching || hub?.state !== 'Connected' || !state) sync(); }, 2000);
    if (!hub) return;
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
  } catch (error) {
    fail(error);
    el('start').disabled = false;
  }
}

el('commit').onclick = () => { if (!el('commit').disabled) move(action, cell); };
el('ready').onclick = () => { if (!el('ready').disabled) move('ready', 0); };
el('copy').onclick = async () => {
  try {
    await navigator.clipboard.writeText(el('link').value);
    el('copy').textContent = 'Ссылка скопирована';
  } catch { el('link').select(); }
};
el('start').onclick = enter;
if (room) {
  el('start').textContent = 'Вступить в дуэль';
  if (!params.has('join') && sessionStorage.getItem(`duel:${room}`)) enter();
}
