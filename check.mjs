import assert from 'node:assert/strict';

// End-to-end check: accounts, friends, invitations, sides, the rules of the game and the history.
// It talks to a running server only through the public API, so it can point at any host with URL=.
const base = process.env.URL || 'http://localhost:5184';
const run = Math.random().toString(36).slice(2, 7);
let passed = 0;
const pass = label => { passed++; console.log(`PASS: ${label}`); };

async function call(path, { method = 'GET', body, token, seat, expect = 200 } = {}) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = `Bearer ${token}`;
  if (seat) headers['X-Seat-Token'] = seat;
  const response = await fetch(base + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body)
  });
  const expected = Array.isArray(expect) ? expect : [expect];
  const text = await response.text();
  assert.ok(expected.includes(response.status), `${method} ${path} → ${response.status}: ${text.slice(0, 240)}`);
  return text ? JSON.parse(text) : null;
}
const post = (path, body, who = {}) => call(path, { method: 'POST', body, ...who });

async function register(prefix) {
  const nickname = `${prefix}${run}`;
  const password = 'duel-pass-42';
  const session = await post('/api/auth/register', { nickname, password });
  assert.ok(session.token, `${prefix} gets a token`);
  assert.equal(session.user.nickname, nickname);
  return { nickname, password, id: session.user.id, token: session.token };
}

// A room opened from a link: no account, the random seat token is the whole credential
async function guestDuel(side = 'defender') {
  const created = await call('/api/duels', { method: 'POST', body: { side }, expect: [200, 201] });
  assert.ok(created.seatToken, 'a guest gets a seat token');
  assert.equal(created.side, side);
  return created;
}
const guest = session => ({ seat: session.seatToken });
const stateOf = (id, who) => call(`/api/duels/${id}/state`, who);
const act = (id, who, wave, action, cell, ok = true) =>
  call(`/api/duels/${id}/moves`, {
    method: 'POST', body: { wave, action, cell }, expect: ok ? 200 : [400, 409], ...who
  });

// ── accounts ────────────────────────────────────────────────────────────────
const anna = await register('anna');
const boris = await register('boris');
const cath = await register('cath');
await call('/api/auth/register', { method: 'POST', body: { nickname: anna.nickname.toUpperCase(), password: 'another-pass' }, expect: 409 });
await call('/api/auth/register', { method: 'POST', body: { nickname: 'ab', password: 'duel-pass-42' }, expect: 400 });
await call('/api/auth/login', { method: 'POST', body: { nickname: anna.nickname, password: 'not-the-one' }, expect: 401 });
const session = await post('/api/auth/login', { nickname: anna.nickname.toUpperCase(), password: anna.password });
assert.ok(session.token, 'login is not case-sensitive in the nickname');
const me = await call('/api/auth/me', { token: session.token });
assert.equal(me.id, anna.id);
const renamed = await call('/api/users/me', { method: 'PUT', token: anna.token, body: { nickname: `${anna.nickname}-x`.slice(0, 24) } });
assert.ok(renamed.nickname.endsWith('-x'), 'the profile keeps a new nickname');
await call('/api/users/me', { method: 'PUT', token: anna.token, body: { nickname: anna.nickname } });
pass('a nickname is unique in any case, a password is checked and a profile can change');

// ── friends ─────────────────────────────────────────────────────────────────
await post('/api/friends/requests', { nickname: boris.nickname }, { token: anna.token, expect: 201 });
await call('/api/friends/requests', { method: 'POST', body: { nickname: anna.nickname }, token: anna.token, expect: 400 });
await call('/api/friends/requests', { method: 'POST', body: { nickname: boris.nickname }, token: anna.token, expect: 409 });
await call('/api/friends/requests', { method: 'POST', body: { nickname: `nobody${run}` }, token: cath.token, expect: 404 });
await call(`/api/friends/${anna.id}`, { method: 'DELETE', token: anna.token, expect: 404 });
const inbox = await call('/api/friends/requests', { token: boris.token });
assert.equal(inbox.incoming.length, 1, 'the addressee sees the request');
assert.equal(inbox.incoming[0].from.id, anna.id);
await call(`/api/friends/requests/${inbox.incoming[0].id}/accept`, { method: 'POST', token: cath.token, expect: 403 });
await post(`/api/friends/requests/${inbox.incoming[0].id}/accept`, {}, { token: boris.token });
const annaFriends = await call('/api/friends', { token: anna.token });
const borisFriends = await call('/api/friends', { token: boris.token });
assert.ok(annaFriends.some(friend => friend.id === boris.id));
assert.ok(borisFriends.some(friend => friend.id === anna.id));
assert.equal(typeof annaFriends[0].online, 'boolean');
pass('a friend request is accepted by the addressee only, and both lists see the pair');

// ── invitation, sides, and the match that goes into the history ─────────────
const room = await post('/api/duels', { side: 'defender' }, { token: anna.token, expect: 201 });
assert.equal(room.side, 'defender');
const invite = await post('/api/invites', { friendId: boris.id, roomId: room.id }, { token: anna.token, expect: 201 });
const invites = await call('/api/invites', { token: boris.token });
assert.ok(invites.some(row => row.id === invite.id && row.from.id === anna.id));
await call('/api/invites', { method: 'POST', body: { friendId: cath.id, roomId: room.id }, token: anna.token, expect: 403 });
await call(`/api/invites/${invite.id}/accept`, { method: 'POST', token: anna.token, expect: 403 });
const accepted = await post(`/api/invites/${invite.id}/accept`, {}, { token: boris.token });
assert.equal(accepted.roomId, room.id);
const joined = await post(`/api/duels/${room.id}/join`, { side: 'attacker' }, { token: boris.token });
assert.equal(joined.side, 'attacker');
await call(`/api/duels/${room.id}/join`, { method: 'POST', body: { side: 'defender' }, token: cath.token, expect: [409, 403] });
pass('a friend is invited to a room, accepts, and takes the free side');

const asked = await post(`/api/duels/${room.id}/swap`, {}, { token: anna.token });
assert.equal(asked.swap, 'mine', 'the asking side waits for an answer');
assert.equal((await stateOf(room.id, { token: boris.token })).swap, 'theirs');
const answered = await post(`/api/duels/${room.id}/swap/accept`, {}, { token: boris.token });
assert.equal(answered.side, 'defender');
assert.equal((await stateOf(room.id, { token: anna.token })).side, 'attacker');
pass('sides change only when the opponent agrees');

// ── the rules of the game, on a guest room ──────────────────────────────────
{
  const host = await guestDuel('defender');
  const id = host.id;
  const A = guest(host);
  const B = guest(await post(`/api/duels/${id}/join`, {side: 'attacker'}));
  const before = await stateOf(id, A);
  const passWave = await act(id, B, 1, 'ready', null);
  assert.equal(passWave.locked, true);
  assert.equal(passWave.last, 0);
  const empty = await act(id, A, 1, 'ready', null);
  assert.equal(empty.last, 1);
  assert.equal(empty.hp, before.hp);
  assert.equal(empty.frames.length, 0);
  const site = empty.paths[0][1], blocked = empty.paths[0][2];
  const mined = await act(id, A, 2, 'mine', site);
  assert.ok(before.trapCells.includes(blocked));
  assert.ok(!mined.trapCells.includes(blocked), 'another trap on that route is not offered');
  await act(id, A, 2, 'spikes', blocked, false);
  await call(`/api/duels/${id}`, {method: 'DELETE', expect: 204, ...A});
  pass('either side can ready an empty wave, and trap sites exclude blocked routes');
}
{
  const host = await guestDuel('defender');
  const roomPath = `/api/duels/${host.id}`;
  const id = host.id;
  const A = guest(host), B = guest(await post(roomPath + '/join', {side: 'attacker'}));
  await act(id, A, 1, 'ready', null);
  await act(id, B, 1, 'ready', null);
  let current = await stateOf(id, B);
  const gate = current.paths[0][0];
  const cap = current.limits?.runner;
  assert.ok(Number.isInteger(cap) && cap >= 1);
  for (let i = 0; i < cap; i++) current = await act(id, B, 2, 'runner', gate);
  assert.ok(current.gold >= current.prices.runner, 'the caller can afford an additional group');
  const error = await post(roomPath + '/moves', {wave: 2, action: 'runner', cell: gate}, {...B, expect: 409});
  assert.equal(error.code, 'purchase_limit');
  const refused = await stateOf(id, B);
  assert.equal(refused.gold, current.gold);
  assert.equal(refused.orders.length, cap);
  await act(id, B, 2, 'cancel', 0);
  assert.equal((await act(id, B, 2, 'runner', gate)).orders.length, cap);
  await call(roomPath, {method: 'DELETE', expect: 204, ...A});
  pass('purchase limits are enforced independently of gold and cancellation frees a place');
}
{
  const host = await guestDuel('defender');
  const visitor = await post(`/api/duels/${host.id}/join`, { side: 'attacker' });
  assert.equal(visitor.side, 'attacker');
  const A = guest(host), B = guest(visitor), id = host.id;
  const s = await stateOf(id, A);
  assert.ok(Array.isArray(s.paths) && s.paths.length >= 2, 'Map has multiple routes');
  assert.deepEqual((await stateOf(id, B)).paths, s.paths);
  const { width, height, paths } = s;
  const distance = (a, b) => Math.abs(a % width - b % width) + Math.abs(Math.floor(a / width) - Math.floor(b / width));
  const keep = paths[0].at(-1), entrances = paths.map(p => p[0]);
  assert.equal(new Set(entrances).size, paths.length);
  for (const route of paths) {
    assert.equal(route.at(-1), keep);
    assert.equal(new Set(route).size, route.length);
    assert.ok(route.every((cell, i) => Number.isInteger(cell) && cell >= 0 && cell < width * height && (!i || distance(cell, route[i - 1]) === 1)));
  }
  const road = new Set([...paths, ...s.alternatePaths].flat());
  const wall = Array.from({ length: width * height }, (_, i) => i)
    .find(i => !road.has(i) && [...road].some(j => distance(i, j) === 1) && !s.towers.some(t => t.cell === i));
  assert.notEqual(wall, undefined);
  await act(id, A, 1, 'swarm', entrances[0], false);
  await act(id, B, 1, 'bolt', wall, false);
  await act(id, A, 1, 'bolt', keep, false);
  await act(id, B, 1, 'swarm', keep, false);
  await act(id, B, 2, 'ready', -1, false);
  const tower = await act(id, A, 1, 'bolt', wall);
  assert.equal(tower.gold, s.gold - s.prices.bolt);
  assert.ok(tower.towers.some(t => t.cell === wall && t.type === 'bolt'));
  await act(id, A, 1, 'bolt', wall, false);
  const attacks = await stateOf(id, B);
  let bought = await act(id, B, 1, 'swarm', entrances[0]);
  assert.equal(bought.gold, attacks.gold - attacks.prices.swarm);
  assert.equal((await stateOf(id, A)).orders.length, 0);
  while (bought.gold >= attacks.prices.swarm) bought = await act(id, B, 1, 'swarm', entrances[1]);
  await act(id, B, 1, 'swarm', entrances[1], false);
  assert.equal((await act(id, A, 1, 'ready', -1)).last, 0);
  assert.equal((await act(id, B, 1, 'ready', -1)).last, 1);
  const wave = await stateOf(id, A);
  assert.ok(wave.frames.some(f => f.mobs.length));
  assert.equal(wave.frames.at(-1).hp, wave.hp);
  await act(id, B, 1, 'swarm', entrances[0], false);
  pass('a guest room keeps the map, the roles, the budget and the private queue');

  // two groups leave one gate one at a time
  const second = await guestDuel('defender');
  const rival = await post(`/api/duels/${second.id}/join`, { side: 'attacker' });
  const A2 = guest(second), B2 = guest(rival), id2 = second.id;
  const view2 = await stateOf(id2, B2);
  const gate = view2.paths[0][0];
  await act(id2, B2, 1, 'swarm', gate);
  await act(id2, B2, 1, 'swarm', gate);
  await act(id2, A2, 1, 'ready', -1);
  const queued = await act(id2, B2, 1, 'ready', -1);
  const atGate = queued.frames.map(f => f.mobs.filter(m => m.y * width + m.x === gate).length);
  assert.equal(Math.max(...atGate), 1, 'Only one slime leaves the same gate at a time');
  assert.equal(queued.hp, view2.hp - 10, 'Both groups reach the undefended keep');
  pass('two groups from the same gate arrive one after another');

  // 48 fresh maps, then the rooms are cancelled
  const maps = new Set();
  let winding = 0;
  const samples = 48;
  for (let i = 0; i < samples; i++) {
    const fresh = await guestDuel('defender');
    const map = await stateOf(fresh.id, guest(fresh));
    const routes = map.paths;
    assert.equal(routes.length, 3);
    const goal = routes[0].at(-1);
    const optionCounts = routes.map(route => 1 + map.alternatePaths.filter(alt => alt[0] === route[0]).length);
    assert.ok(optionCounts.every(count => count === 2 || count === 4));
    assert.ok(optionCounts.includes(4));
    for (const alt of map.alternatePaths) {
      const primary = routes.find(route => route[0] === alt[0]);
      assert.equal(alt.at(-1), goal);
      assert.ok(primary && alt.length > primary.length);
      assert.equal(new Set(alt).size, alt.length);
      assert.ok(alt.every((cell, k) => cell >= 0 && cell < width * height && (!k || distance(cell, alt[k - 1]) === 1)));
    }
    const uses = new Map();
    for (const route of routes) {
      assert.equal(route.at(-1), goal);
      assert.equal(new Set(route).size, route.length);
      assert.ok(route.every((cell, j) => cell >= 0 && cell < width * height && (!j || distance(cell, route[j - 1]) === 1)));
      for (const cell of route) uses.set(cell, (uses.get(cell) || 0) + 1);
    }
    if (routes.every(route => route.length <= 39 && route.filter((cell, j) => j >= 2 && cell - route[j - 1] !== route[j - 1] - route[j - 2]).length >= 4 && route.filter(cell => uses.get(cell) === 1).length >= 6)) winding++;
    maps.add(JSON.stringify(routes.map(route => route.slice(1))));
    await call(`/api/duels/${fresh.id}`, { method: 'DELETE', ...guest(fresh), expect: 204 });
    await call(`/api/duels/${fresh.id}/state`, { ...guest(fresh), expect: 404 });
  }
  assert.ok(maps.size >= samples * 0.9, `Only ${maps.size}/${samples} distinct interiors`);
  assert.ok(winding >= samples * 0.9, `Only ${winding}/${samples} maps have three winding approaches`);
  console.log(`${winding}/${samples} maps have winding approaches; ${maps.size} distinct interiors`);
  pass('every room has a fresh maze with three winding ways to the keep');
}

// ── fences, smart slimes, structures ───────────────────────────────────────
{
  const fenced = await guestDuel('defender');
  const raider = await post(`/api/duels/${fenced.id}/join`, { side: 'attacker' });
  const A = guest(fenced), B = guest(raider), id = fenced.id;
  const view = await stateOf(id, A);
  const shortest = view.paths[0], alt = view.alternatePaths[0];
  const barrier = shortest.find(c => !alt.includes(c));
  assert.equal(view.prices.smart, 5);
  assert.ok(view.fenceCells.includes(barrier));
  await act(id, A, 1, 'fence', shortest[0], false);
  await act(id, A, 1, 'fence', barrier);
  await act(id, A, 1, 'fence', barrier, false);
  await act(id, B, 1, 'smart', shortest[0]);
  await act(id, A, 1, 'ready', -1);
  const siege = await act(id, B, 1, 'ready', -1);
  const reached = new Set();
  let intact = false;
  for (const frame of siege.frames) {
    const blocked = frame.fences.some(f => f.cell === barrier);
    intact ||= blocked;
    for (const mob of frame.mobs) {
      assert.equal(mob.kind, 'smart');
      const cell = mob.y * view.width + mob.x;
      assert.ok(shortest.includes(cell));
      if (blocked) assert.ok(shortest.indexOf(cell) < shortest.indexOf(barrier));
      reached.add(cell);
    }
  }
  assert.ok(intact && reached.has(barrier) && !siege.fences.length);
  pass('forks, the smart slime price, its shortest path and a fence that blocks until destroyed');
}

{
  const host = await guestDuel();
  const id = host.id;
  const waiting = await stateOf(id, guest(host));
  assert.equal(waiting.deadline, null, 'the clock does not run without an opponent');
  const raid = await post(`/api/duels/${id}/join`, { side: 'attacker' });
  const before = await stateOf(id, guest(host));
  assert.ok(before.secondsLeft >= 59 && before.secondsLeft <= 60, 'the clock starts at one minute');
  await act(id, guest(raid), 1, 'apc', before.paths[0][0]);
  await act(id, guest(host), 1, 'ready');
  const after = await act(id, guest(raid), 1, 'ready');
  const deployment = after.frames.findIndex(f => f.mobs.some(m => m.kind === 'balaclava'));
  assert.ok(deployment >= 20, 'the transport moves at most once per four ticks');
  const position = m => m.y * before.width + m.x;
  const visited = after.frames.slice(0, deployment).flatMap(f => f.mobs.filter(m => m.kind === 'apc')).map(position);
  const firstPassenger = after.frames[deployment].mobs.find(m => m.kind === 'balaclava');
  const prefix = [...new Set([...visited, position(firstPassenger)])];
  assert.ok([...before.paths, ...before.alternatePaths].some(path =>
    prefix.length === 1 + Math.ceil(0.7 * (path.length - 1)) && prefix.every((cell, i) => path[i] === cell)),
    'the carrier stops after 70% of its path');
  const released = new Map();
  for (const [tick, frame] of after.frames.entries()) {
    const troops = frame.mobs.filter(m => m.kind === 'balaclava');
    assert.equal(new Set(troops.map(position)).size, troops.length);
    for (const mob of troops) if (!released.has(mob.id)) released.set(mob.id, tick);
  }
  assert.equal(released.size, 5, 'the transport carries five');
  assert.equal(new Set(released.values()).size, 5, 'passengers leave on separate ticks');
  assert.ok(after.frames.slice(Math.max(...released.values())).every(f => f.mobs.every(m => m.kind !== 'apc')));
  assert.equal(after.hp, before.hp - 10, 'each of the five hits for two hearts');
  assert.equal(after.done, true, 'ten hearts fall to one loaded transport');
  pass('a transport of five unloads one by one and takes a ten-heart keep at once');
}
{
  const host = await guestDuel();
  const id = host.id;
  const raid = await post(`/api/duels/${id}/join`, { side: 'attacker' });
  const before = await stateOf(id, guest(host));
  const {paths, alternatePaths, width: w, height: h} = before;
  const road = new Set([...paths, ...alternatePaths].flat());
  const wall = paths[0].slice(1, -1).flatMap(c =>
    [c % w ? c - 1 : -1, c % w < w - 1 ? c + 1 : -1, c - w, c + w])
    .find(c => c >= 0 && c < w * h && !road.has(c));
  assert.notEqual(wall, undefined);
  await act(id, guest(host), 1, 'frost', wall);
  await act(id, guest(raid), 1, 'smart', paths[0][0]);
  await act(id, guest(host), 1, 'ready');
  const after = await act(id, guest(raid), 1, 'ready');
  assert.ok(after.frames.some(f => f.mobs.some(m => m.frozen)), 'the frost tower applies its effect');
  assert.equal(after.hp, before.hp - 5, 'each slime thaws and reaches the keep');
  assert.ok(Date.parse(after.deadline) - Date.now() >= 59000 + after.replayLeftMs,
    'the replay does not use planning time');
  pass('frost slows slimes without stopping the simulation, and the replay leaves a full minute');
}

// ── income, the end of a match and the rematch ─────────────────────────────
let end, defenderView;
const played = [];
{
  const id = room.id, A = { token: anna.token }, B = { token: boris.token };
  end = await stateOf(id, A);
  defenderView = await stateOf(id, B);
  await call(`/api/matches/${end.matchId}/replay`, { expect: 404 });
  for (let wave = 1; wave <= 5 && !end.done; wave++) {
    const before = end.hp;
    let purchased = await act(id, A, wave, 'swarm', end.paths[0][0]);
    if (wave === 1) purchased = await act(id, A, wave, 'swarm', end.paths[1][0]);
    await act(id, B, wave, 'ready', -1);
    end = await act(id, A, wave, 'ready', -1);
    played.push(end);
    const nextDefender = await stateOf(id, B);
    const budgets = [[12, 17], [14, 20], [16, 24], [18, 29]];
    assert.deepEqual([nextDefender.gold, end.gold], end.done ? [defenderView.gold, purchased.gold] : budgets[wave - 1]);
    defenderView = nextDefender;
    assert.ok(end.hp < before);
    assert.ok(end.frames.some((f, i) => i && f.hp < end.frames[i - 1].hp));
  }
  assert.equal(end.done, true);
  assert.equal(end.hp, 0);
  const closed = (await call('/api/duels', A)).find(row => row.id === id);
  assert.equal(closed.status, 'Finished');
  assert.equal(closed.replayId, end.matchId);
  const journal = await call('/api/matches/mine', A);
  const recorded = journal.find(row => row.id === end.matchId);
  assert.ok(recorded?.hasReplay && Number.isFinite(Date.parse(recorded.finishedAt)));
  await act(id, A, end.last + 1, 'swarm', end.paths[0][0], false);
  const restarted = await act(id, A, end.last + 1, 'rematch', -1);
  assert.equal(restarted.last, 0, 'a rematch starts from the first wave');
  assert.equal(restarted.done, false);
  assert.ok(restarted.hp > 0);
  pass('gold resets to the wave budget, a keep that falls ends the match and a rematch resets it');
}

// ── history, notes, statistics ─────────────────────────────────────────────
let match;
for (let i = 0; i < 24 && !match; i++) {
  const recent = await call('/api/matches?take=20');
  match = recent.find(row => row.roomId === room.id);
  if (!match) await new Promise(resolve => setTimeout(resolve, 250));
}
assert.ok(match, 'the finished match reaches PostgreSQL');
assert.equal(match.winner, 'Attacker');
const detail = await call(`/api/matches/${match.id}`);
assert.equal(detail.waves.length, detail.wavesPlayed);
const replayUrl = `/api/matches/${match.id}/replay`;
assert.deepEqual(await call(replayUrl), { width: end.width, height: end.height, paths: end.paths,
  alternatePaths: end.alternatePaths, waveCount: played.length });
for (const wave of played) {
  assert.deepEqual(await call(`${replayUrl}/${wave.last}`), {
    replayMs: wave.replayMs, replayStart: wave.replayStart, frames: wave.frames,
  });
}
await call(`${replayUrl}/${played.length + 1}`, { expect: 404 });
pass('anonymous viewers can read the exact battle frames, one wave at a time, after a rematch');
const noted = await call(`/api/matches/${match.id}`, { method: 'PATCH', token: anna.token, body: { note: 'реванш' } });
assert.equal(noted.note, 'реванш');
await call(`/api/matches/${match.id}`, { method: 'PATCH', token: cath.token, body: { note: 'чужое' }, expect: 403 });
const stats = await call('/api/stats');
assert.ok(stats.matches >= 1);
assert.ok(stats.waves.length > 0);
const board = await call('/api/leaderboard?take=5');
assert.ok(board.some(row => row.id === anna.id), 'the winner is on the leaderboard');
await call(`/api/matches/${match.id}`, { method: 'DELETE', token: cath.token, expect: 403 });
await call(`/api/matches/${match.id}`, { method: 'DELETE', token: boris.token, expect: 204 });
await call(`/api/matches/${match.id}`, { expect: 404 });
await call(replayUrl, { expect: 404 });
await call(`${replayUrl}/1`, { expect: 404 });
pass('a finished match is stored, noted, listed by LINQ and deleted by a participant only');

// ── the room lifecycle through the account API ─────────────────────────────
{
  const attempts = await Promise.all([1, 2].map(() => fetch(`${base}/api/duels`, {
    method: 'POST', headers: { Authorization: `Bearer ${cath.token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({side: 'attacker'}),
  })));
  assert.deepEqual(attempts.map(response => response.status).sort(), [201, 409]);
  const extra = await attempts.find(response => response.status === 201).json();
  await post(`/api/duels/${extra.id}/join`, {}, { token: anna.token, expect: 409 });
  await call(`/api/duels/${room.id}`, { method: 'DELETE', token: boris.token, expect: 204 });
  await call(`/api/duels/${room.id}/state`, { token: anna.token, expect: 404 });
  const mine = await call('/api/duels', { token: cath.token });
  assert.ok(mine.some(row => row.id === extra.id && row.mySide === 'attacker'));
  const swappedAlone = await post(`/api/duels/${extra.id}/swap`, {}, { token: cath.token });
  assert.equal(swappedAlone.side, 'defender', 'a lone player moves to the other side at once');
  await post(`/api/duels/${extra.id}/join`, {}, { token: anna.token });
  await call(`/api/duels/${extra.id}`, { method: 'DELETE', token: cath.token, expect: 204 });
  await call(`/api/duels/${extra.id}/state`, { token: anna.token, expect: 404 });
  pass('one active duel per account, even in parallel; leaving frees both players');
}

async function roomWatcher(token) {
  const url = new URL('/bell', base);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.searchParams.set('access_token', token);
  const ws = new WebSocket(url);
  let invocation = 0;
  const send = async (frame, responseFor) => {
    const reply = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(Error('SignalR did not reply')), 5000);
      ws.addEventListener('message', event => {
        for (const raw of String(event.data).split('\x1e').filter(Boolean)) {
          const message = JSON.parse(raw);
          if (!responseFor(message)) continue;
          clearTimeout(timeout);
          if (message.error) reject(Error(message.error)); else resolve(message);
        }
      }, {signal: AbortSignal.timeout(5000)});
    });
    ws.send(JSON.stringify(frame) + '\x1e');
    return reply;
  };
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(Error('SignalR did not connect')), 5000);
      ws.addEventListener('open', () => { clearTimeout(timer); resolve(); }, {once: true});
      ws.addEventListener('error', () => { clearTimeout(timer); reject(Error('SignalR connection failed')); }, {once: true});
    });
    await send({protocol: 'json', version: 1}, message => !message.type);
    return {close: () => ws.close(), invoke(target, ...args) {
      const invocationId = String(++invocation);
      return send({type: 1, invocationId, target, arguments: args}, msg => msg.type === 3 && msg.invocationId === invocationId);
    }};
  } catch (error) {
    ws.close();
    throw error;
  }
}

// ── random matchmaking ────────────────────────────────────────────────────
{
  const ana = {token: anna.token}, bor = {token: boris.token}, cat = {token: cath.token};
  await post('/api/duels/random/join', undefined, {expect: 401});
  const privateRoom = await post('/api/duels', {side: 'defender'}, {...ana, expect: 201});
  await post('/api/duels/random/join', undefined, {...bor, expect: 204});
  await call(`/api/duels/${privateRoom.id}`, {...ana, method: 'DELETE', expect: 204});
  const createOpen = who => post('/api/duels', {side: 'attacker', randomOpponent: true}, {...who, expect: 201});
  const older = await createOpen(ana);
  const newer = await createOpen(bor);
  await post('/api/duels/random/join', undefined, {...cat, expect: 204});
  const watcherA = await roomWatcher(ana.token);
  const watcherB = await roomWatcher(bor.token);
  try {
    await watcherA.invoke('JoinRoom', older.id, null);
    await watcherB.invoke('JoinRoom', newer.id, null);
    await post('/api/duels/random/join', undefined, {...ana, expect: 409});
    const newest = await post('/api/duels/random/join', undefined, cat);
    assert.equal(newest.id, newer.id, 'the last room in is the first room used');
    assert.equal(newest.side, 'defender');
    await call(`/api/duels/${newer.id}`, {...bor, method: 'DELETE', expect: 204});
    const offline = await createOpen(bor);
    await watcherB.invoke('JoinRoom', offline.id, null);
    await watcherB.invoke('LeaveRoom', offline.id);
    const attempts = await Promise.all([cat, bor].map(who => post('/api/duels/random/join', undefined,
      {...who, expect: who === bor ? 409 : 200})));
    assert.equal(attempts[0].id, older.id, 'skip the newest room when its host no longer watches it');
    await call(`/api/duels/${older.id}`, {...ana, method: 'DELETE', expect: 204});
    await call(`/api/duels/${offline.id}`, {...bor, method: 'DELETE', expect: 204});
    const race = await createOpen(ana);
    await watcherA.invoke('JoinRoom', race.id, null);
    const joined = await Promise.all([bor,cat].map(who => post('/api/duels/random/join', undefined, {...who, expect: [200,204]})));
    assert.equal(joined.filter(result => result?.id === race.id).length, 1);
    assert.equal(joined.filter(result => result === null).length, 1);
    await call(`/api/duels/${race.id}`, {...ana, method: 'DELETE', expect: 204});
  } finally {
    watcherA.close();
    watcherB.close();
  }
  pass('random matchmaking takes the latest live room and assigns each seat only once');
}

// ── invite cleanup on refusal and match start ──────────────────────────────
{
  const host = {token: anna.token}, opponent = {token: boris.token}, third = {token: cath.token};
  const create = who => post('/api/duels', {side: 'defender'}, {...who, expect: 201});
  const send = (from, to, roomId) => post('/api/invites', {friendId: to.id, roomId}, {...from, expect: 201});
  const list = who => call('/api/invites', who);
  const close = (who, id) => call(`/api/duels/${id}`, {...who, method: 'DELETE', expect: 204});
  const waiting = await create(host);
  const refused = await send(host, boris, waiting.id);
  await call(`/api/invites/${refused.id}`, {...opponent, method: 'DELETE', expect: 204});
  await call(`/api/duels/${waiting.id}/state`, {...host, expect: 404});
  assert.equal((await call('/api/duels', host)).find(row => row.id === waiting.id)?.status, 'Cancelled');

  await post('/api/friends/requests', {nickname: anna.nickname}, {...third, expect: 201});
  const requests = await call('/api/friends/requests', host);
  const fromCath = requests.incoming.find(request => request.from.id === cath.id);
  await post(`/api/friends/requests/${fromCath.id}/accept`, {}, host);
  const otherRoom = await create(third);
  const incoming = await send(third, anna, otherRoom.id);
  const live = await create(host);
  const outgoing = await send(host, boris, live.id);
  assert.ok((await list(host)).some(invite => invite.id === incoming.id));
  assert.ok((await list(opponent)).some(invite => invite.id === outgoing.id));
  await post(`/api/duels/${live.id}/join`, {}, opponent);
  assert.ok(!(await list(host)).some(invite => invite.id === incoming.id));
  assert.ok(!(await list(opponent)).some(invite => invite.id === outgoing.id));
  await call(`/api/invites/${outgoing.id}`, {...opponent, method: 'DELETE', expect: 409});
  assert.equal((await stateOf(live.id, host)).joined, true);
  await close(host, live.id);
  await close(third, otherRoom.id);
  await call(`/api/friends/${cath.id}`, {...host, method: 'DELETE', expect: 204});
  pass('decline closes a waiting room; a match start cancels pending invitations for both players');
}

// ── removing a friend and an account ───────────────────────────────────────
await call(`/api/friends/${boris.id}`, { method: 'DELETE', token: cath.token, expect: 404 });
await call(`/api/friends/${boris.id}`, { method: 'DELETE', token: anna.token, expect: 204 });
assert.equal((await call('/api/friends', { token: anna.token })).length, 0);
await call('/api/users/me', { method: 'DELETE', token: cath.token, body: { password: 'wrong-pass' }, expect: 401 });
await call('/api/users/me', { method: 'DELETE', token: cath.token, body: { password: cath.password }, expect: 204 });
await call(`/api/users/${cath.id}`, { expect: 404 });
// The run leaves no accounts behind, so the check can be repeated against one database
for (const who of [anna, boris]) await call('/api/users/me', {
  method: 'DELETE', token: who.token, body: { password: who.password }, expect: 204
});
pass('a friend can be removed and an account deleted after a password check');

console.log(`\n${passed} groups of checks passed`);
