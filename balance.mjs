const base = process.env.URL || 'http://localhost:5184';
const samples = Number(process.env.SAMPLES || 12);
const defenders = ['keep', 'spread', 'mixed'];
const attackers = ['swarm', 'giant', 'smart', 'runner', 'armored', 'ranged', 'apc', 'mixed'];
if (!Number.isSafeInteger(samples) || samples < 1) throw Error('SAMPLES must be a positive integer');

function selected(key, available) {
  const options = process.env[key]?.split(',') ?? available;
  for (const name of options) if (!available.includes(name)) throw Error(`Unknown ${key}: ${name}`);
  return options;
}

async function call(path, { method = 'GET', body, seat } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (seat) headers['X-Seat-Token'] = seat;
  const r = await fetch(base + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(10000),
  });
  if (!r.ok) throw Error(`${method} ${path} ${JSON.stringify(body || {})}: ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}

function field(s) {
  const all = [...s.paths, ...s.alternatePaths];
  const road = new Set(all.flat());
  const dist = (u, v) => (u % s.width - v % s.width) ** 2 +
    (Math.floor(u / s.width) - Math.floor(v / s.width)) ** 2;
  const walls = Array.from({length: s.width * s.height}, (_, i) => i)
    .filter(i => !road.has(i) && [...road].some(j => dist(i, j) === 1));
  const keep = s.paths[0].at(-1);
  const route = s.paths.map(path => all.filter(p => p[0] === path[0]));
  const inReach = (type, site, cell) => dist(site, cell) <= ({bolt: 9, pulse: 4, cannon: 8, frost: 2, spikes: 0, mine: 0})[type];
  return {dist, walls, keep, route, inReach};
}

function placements(strategy, state, map) {
  const {gold, prices, towers, fences, fenceCells} = state;
  const placed = type => towers.filter(t => t.type === type).length;
  const types = strategy === 'keep' ? ['bolt'] : strategy === 'spread' ? ['pulse', 'bolt'] :
    !placed('bolt') ? ['bolt', 'pulse'] : !placed('pulse') ? ['pulse', 'bolt'] :
    !placed('frost') ? ['frost', 'bolt', 'pulse'] : ['cannon', 'bolt', 'pulse'];
  const occupied = new Set([...towers, ...fences].map(t => t.cell));
  if (strategy === 'mixed' && placed('pulse') && placed('bolt') && !fences.length && gold >= prices.fence) {
    const [cell] = fenceCells.filter(c => !occupied.has(c) && towers.some(t => map.inReach(t.type, t.cell, c)))
      .sort((a, b) => map.dist(a, map.keep) - map.dist(b, map.keep));
    if (cell !== undefined) return {type: 'fence', cell};
  }
  for (const type of types) {
    if (gold < prices[type] || placed(type) >= (state.limits?.[type] ?? Infinity)) continue;
    const score = cell => strategy === 'keep' ? -map.dist(cell, map.keep) : map.route.reduce((total, variants) =>
      total + variants.reduce((sum, path) => sum + path.reduce((n, roadCell) => {
        if (!map.inReach(type, cell, roadCell)) return n;
        const overlap = towers.filter(t => map.inReach(t.type, t.cell, roadCell));
        return n + (strategy === 'spread' ? 1 / (1 + overlap.length) :
          (type === 'frost' ? 1 + overlap.length : 1) * (1 + 1 / (1 + map.dist(map.keep, roadCell))));
      }, 0), 0) / variants.length, 0);
    const spots = map.walls.filter(c => !occupied.has(c)).map(c => ({c, score: score(c)}));
    spots.sort((a, b) => b.score - a.score);
    if (spots.length) return {type, cell: spots[0].c};
  }
  return null;
}

function orders(strategy, state, board, map, sample) {
  const count = type => state.orders.filter(o => o.type === type).length;
  const mix = board.towers.length ? ['ranged', 'giant', 'runner', 'smart', 'swarm'] : ['runner', 'swarm'];
  const prefers = strategy === 'mixed' ? mix.sort((a, b) => count(a) - count(b)) : [strategy];
  const type = [...new Set([...prefers, 'swarm', 'runner'])].find(t =>
    state.gold >= state.prices[t] && count(t) < (state.limits?.[t] ?? Infinity));
  if (!type) return null;
  const weigh = path => path.reduce((sum, c) => sum + board.towers.filter(t => map.inReach(t.type, t.cell, c)).length +
    board.fences.filter(f => f.cell === c).length * 3, 0);
  const routes = map.route.map(paths => {
    const fastest = paths.reduce((best, p) => p.length < best.length ? p : best);
    const exposure = type === 'smart' ? weigh(fastest) :
      paths.reduce((s, p) => s + weigh(p), 0) / paths.length;
    return {gate: fastest[0], exposure};
  }).sort((a, b) => a.exposure - b.exposure);
  const i = strategy === 'swarm' || strategy === 'runner' ? (sample + state.orders.length) % routes.length : 0;
  return {type, cell: routes[i].gate};
}

async function match(defense, attack, n) {
  const d = await call('/api/duels', {method: 'POST', body: {side: 'defender'}});
  const room = `/api/duels/${d.id}`;
  try {
    const a = await call(room + '/join', {method: 'POST', body: {side: 'attacker'}});
    const view = p => call(room + '/state', {seat: p.seatToken});
    const buy = (p, wave, action, cell) => call(room + '/moves', {method: 'POST', body: {wave, action, cell}, seat: p.seatToken});
    let ds = await view(d), as = await view(a);
    const map = field(ds), startHp = ds.hp;
    let openingHp = startHp;
    for (let wave = 1; !ds.done; wave++) {
      for (let move; (move = placements(defense, ds, map));) ds = await buy(d, wave, move.type, move.cell);
      for (let move; (move = orders(attack, as, ds, map, n));) as = await buy(a, wave, move.type, move.cell);
      await buy(d, wave, 'ready');
      as = await buy(a, wave, 'ready');
      ds = await view(d);
      if (wave === 1) openingHp = ds.hp;
    }
    return {hp: ds.hp, waves: ds.last, openingHp, openingDamage: startHp - openingHp};
  } finally {
    await call(room, {method: 'DELETE', seat: d.seatToken});
  }
}

const mean = (rows, key) => (rows.reduce((sum, row) => sum + row[key], 0) / rows.length).toFixed(1);
for (const defense of selected('DEFENSE', defenders)) for (const attack of selected('ATTACK', attackers)) {
  const rows = [];
  for (let i = 0; i < samples; i++) rows.push(await match(defense, attack, i));
  const wins = rows.filter(row => row.hp === 0).length;
  const first = rows.filter(row => row.openingHp === 0).length;
  console.log(`${defense} vs ${attack}: wins ${wins}/${samples}, first wave ${first}/${samples}, ` +
    `opening damage ${mean(rows, 'openingDamage')}, final HP ${mean(rows, 'hp')}, waves ${mean(rows, 'waves')}`);
}
