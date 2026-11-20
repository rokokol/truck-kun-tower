const base = process.env.URL || 'http://localhost:5184';
const samples = Number(process.env.SAMPLES || 12);

async function call(path, { method = 'GET', body, seat } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (seat) headers['X-Seat-Token'] = seat;
  const r = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok) throw Error(`${method} ${path} ${JSON.stringify(body || {})}: ${r.status}`);
  return r.status === 204 ? null : r.json();
}

// Two guests: balance plays the same rooms the link flow opens, no accounts needed
async function match(defense, attack, n) {
  const d = await call('/api/duels', { method: 'POST', body: { side: 'defender' } });
  const room = `/api/duels/${d.id}`;
  const a = await call(room + '/join', { method: 'POST', body: { side: 'attacker' } });
  const view = p => call(room + '/state', { seat: p.seatToken });
  const buy = (p, wave, action, cell) => call(room + '/moves', { method: 'POST', body: { wave, action, cell }, seat: p.seatToken });
  let ds = await view(d), as = await view(a);
  const paths = [...ds.paths, ...ds.alternatePaths], road = new Set(paths.flat()), keep = ds.paths[0].at(-1);
  const dx = (u, v) => u % ds.width - v % ds.width, dy = (u, v) => Math.floor(u / ds.width) - Math.floor(v / ds.width);
  const dist = (u, v) => dx(u, v) ** 2 + dy(u, v) ** 2;
  const walls = Array.from({ length: ds.width * ds.height }, (_, i) => i)
    .filter(i => !road.has(i) && [...road].some(j => dist(i, j) === 1));
  for (let wave = 1; !ds.done; wave++) {
    if (defense === 'keep' || defense === 'spread') {
      const types = defense === 'keep' ? ['bolt'] : ['pulse', 'bolt'];
      while (true) {
        const type = types.find(t => ds.gold >= ds.prices[t]); if (!type) break;
        const spots = walls.filter(i => !ds.towers.some(t => t.cell === i)); if (!spots.length) break;
        const score = i => defense === 'keep' ? -dist(i, keep) : paths.reduce((total, p) => total + p.reduce((sum, j) => sum + (dist(i, j) <= (type === 'bolt' ? 9 : 4) ? 1 / (1 + ds.towers.filter(t => dist(t.cell, j) <= (t.type === 'bolt' ? 9 : 4)).length) : 0), 0), 0);
        spots.sort((u, v) => score(v) - score(u)); ds = await buy(d, wave, type, spots[0]);
      }
    }
    const costs = as.prices;
    for (let i = 0; as.gold >= costs.swarm; i++) {
      const type = attack === 'giant' && as.gold >= costs.giant ? 'giant' : attack === 'smart' && costs.smart && as.gold >= costs.smart ? 'smart' : 'swarm';
      const weigh = p => p.reduce((sum, c) => sum + ds.towers.reduce((v, t) => v + (dist(t.cell, c) <= (t.type === 'bolt' ? 9 : 4) ? 1 : 0), 0), 0);
      const routes = [...as.paths].sort((u, v) => weigh(u) - weigh(v));
      const route = attack === 'swarm' ? routes[(n + i) % routes.length] : routes[0];
      as = await buy(a, wave, type, route[0]);
    }
    await buy(d, wave, 'ready'); as = await buy(a, wave, 'ready'); ds = await view(d);
  }
  return [ds.hp, ds.last];
}

for (const defense of ['keep', 'spread']) for (const attack of ['swarm', 'giant', 'smart']) {
  const results = [];
  for (let i = 0; i < samples; i++) results.push(await match(defense, attack, i));
  const wins = results.filter(([hp]) => hp === 0).length;
  const hp = results.reduce((sum, [hp]) => sum + hp, 0) / samples;
  const waves = results.reduce((sum, [, wave]) => sum + wave, 0) / samples;
  console.log(`${defense} vs ${attack}: attack wins ${wins}/${samples}, mean HP ${hp.toFixed(1)}, mean waves ${waves.toFixed(1)}`);
}
