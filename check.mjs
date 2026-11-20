import assert from 'node:assert/strict';
const base = process.env.URL || 'http://localhost:5184';
const page = await (await fetch(base + '/')).text();
assert.match(page, /telegram-web-app\.js/, 'The page loads the Telegram bridge');
assert.match(page, /viewport-fit=cover/, 'The page respects the phone safe area');
const config = await (await fetch(base + '/config')).json();
assert.ok('telegram' in config, 'The server names the Telegram app');
async function post(path, data={}, ok=true) {
  const r = await fetch(base+path, {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(data)});
  assert.equal(r.ok, ok, `${path} ${JSON.stringify(data)}: ${r.status}`);
  return ok ? r.json() : null;
}
const a = await post('/room'), path = '/room/'+a.id;
const b = await post(path+'/join');
await post(path+'/join', {}, false);
const state = ({key}) => post(path+'/state', {key});
const move = ({key},action,cell,ok=true,wave=1) => post(path+'/move', {key,wave,action,cell}, ok);
const s = await state(a);
assert.ok(Array.isArray(s.paths) && s.paths.length >= 2, 'Map has multiple routes');
assert.deepEqual((await state(b)).paths,s.paths);
const {width,height,paths} = s;
const distance = (a,b) => Math.abs(a%width-b%width)+Math.abs(Math.floor(a/width)-Math.floor(b/width));
const keep = paths[0].at(-1), entrances = paths.map(p=>p[0]);
assert.equal(new Set(entrances).size, paths.length);
for (const route of paths) {
  assert.equal(route.at(-1),keep);
  assert.equal(new Set(route).size,route.length);
  assert.ok(route.every((cell,i) => Number.isInteger(cell) && cell>=0 && cell<width*height && (!i || distance(cell,route[i-1])===1)));
}
const road = new Set([...paths, ...s.alternatePaths].flat());
const wall = Array.from({length:width*height},(_,i)=>i).find(i=>!road.has(i)&&![...road].every(j=>distance(i,j)!==1)&&!s.towers.some(t=>t.cell===i));
assert.notEqual(wall,undefined);
await move(a,'swarm',entrances[0],false);
await move(b,'bolt',wall,false);
await move(a,'bolt',keep,false);
await move(b,'swarm',keep,false);
await move(b,'ready',-1,false);
const tower = await move(a,'bolt',wall);
assert.equal(tower.gold,s.gold-s.prices.bolt);
assert.ok(tower.towers.some(t=>t.cell===wall&&t.type==='bolt'));
await move(a,'bolt',wall,false);
const attacks = await state(b);
let bought = await move(b,'swarm',entrances[0]);
assert.equal(bought.gold,attacks.gold-attacks.prices.swarm);
assert.equal((await state(a)).orders.length,0);
while(bought.gold>=attacks.prices.swarm) bought=await move(b,'swarm',entrances[1]);
await move(b,'swarm',entrances[1],false);
assert.equal((await move(a,'ready',-1)).last,0);
assert.equal((await move(b,'ready',-1)).last,1);
const wave = await state(a);
assert.ok(wave.frames.some(f=>f.mobs.length));
assert.equal(wave.frames.at(-1).hp,wave.hp);
await move(b,'swarm',entrances[0],false);
const queueHost = await post('/room');
const queueRoom = `/room/${queueHost.id}`;
const queueGuest = await post(queueRoom+'/join');
const queueState = await post(queueRoom+'/state', {key:queueGuest.key});
const gate = queueState.paths[0][0];
const queueMove = (p,action,cell) => post(queueRoom+'/move',{key:p.key,wave:1,action,cell});
await queueMove(queueGuest,'swarm',gate);
await queueMove(queueGuest,'swarm',gate);
await queueMove(queueHost,'ready');
const queuedWave = await queueMove(queueGuest,'ready');
const atGate = queuedWave.frames.map(f=>f.mobs.filter(m=>m.y*width+m.x===gate).length);
assert.equal(Math.max(...atGate),1,'Only one slime leaves the same gate at a time');
assert.equal(queuedWave.hp,queueState.hp-10,'Both groups reach the undefended keep');
console.log('PASS: two groups leave the same gate in sequence');
const maps = new Set();
let winding = 0;
const samples = 48;
for (let i=0;i<samples;i++) {
  const fresh=await post('/room');
  const map=await post(`/room/${fresh.id}/state`, {key:fresh.key});
  const routes=map.paths;
  assert.equal(routes.length,3);
  const goal=routes[0].at(-1);
  const optionCounts=routes.map(route=>1+map.alternatePaths.filter(alt=>alt[0]===route[0]).length);
  assert.ok(optionCounts.every(count=>count===2||count===4));
  assert.ok(optionCounts.includes(4));
  for (const alt of map.alternatePaths) {
    const primary=routes.find(route=>route[0]===alt[0]);
    assert.equal(alt.at(-1),goal);
    assert.ok(primary && alt.length>primary.length);
    assert.equal(new Set(alt).size,alt.length);
    assert.ok(alt.every((cell,k)=>cell>=0 && cell<width*height && (!k || distance(cell,alt[k-1])===1)));
  }
  const uses=new Map();
  for (const route of routes) {
    assert.equal(route.at(-1),goal);
    assert.equal(new Set(route).size,route.length);
    assert.ok(route.every((cell,j)=>cell>=0 && cell<width*height && (!j || distance(cell,route[j-1])===1)));
    for (const cell of route) uses.set(cell,(uses.get(cell)||0)+1);
  }
  if (routes.every(route=>route.length<=39 && route.filter((cell,j)=>j>=2 && cell-route[j-1]!==route[j-1]-route[j-2]).length>=4 && route.filter(cell=>uses.get(cell)===1).length>=6)) winding++;
  maps.add(JSON.stringify(routes.map(route=>route.slice(1))));
}
assert.ok(maps.size>=samples*0.9,`Only ${maps.size}/${samples} distinct interiors`);
assert.ok(winding>=samples*0.9,`Only ${winding}/${samples} maps have three winding approaches`);
console.log(`${winding}/${samples} maps have winding approaches; ${maps.size} distinct interiors`);
const fenced = await post('/room');
const fencedRoom = `/room/${fenced.id}`;
const raider = await post(fencedRoom+'/join');
const fencedState = await post(fencedRoom+'/state', {key:fenced.key});
const shortest = fencedState.paths[0], alt = fencedState.alternatePaths[0];
const barrier = shortest.find(c=>!alt.includes(c));
const fencedMove = (player,action,cell,ok=true)=>post(fencedRoom+'/move',{key:player.key,wave:1,action,cell},ok);
assert.equal(fencedState.prices.smart, 5);
assert.ok(fencedState.fenceCells.includes(barrier));
await fencedMove(fenced,'fence',shortest[0],false);
await fencedMove(fenced,'fence',barrier);
await fencedMove(fenced,'fence',barrier,false);
await fencedMove(raider,'smart',shortest[0]);
await fencedMove(fenced,'ready');
const siege = await fencedMove(raider,'ready');
const reached = new Set();
let intact = false;
for (const frame of siege.frames) {
  const blocked = frame.fences.some(f=>f.cell===barrier);
  intact ||= blocked;
  for (const mob of frame.mobs) {
    assert.equal(mob.kind,'smart');
    const cell=mob.y*width+mob.x;
    assert.ok(shortest.includes(cell));
    if (blocked) assert.ok(shortest.indexOf(cell)<shortest.indexOf(barrier));
    reached.add(cell);
  }
}
assert.ok(intact && reached.has(barrier) && !siege.fences.length);
console.log('PASS: forks, smart unit price and shortest path, fence blocks until destroyed');
const undefended = await post('/room');
const open = `/room/${undefended.id}`;
const invader = await post(open+'/join');
let end = await post(open+'/state', {key:invader.key});
let defender = await post(open+'/state', {key:undefended.key});
let previousIncome;
for (let wave=1; wave<=5 && !end.done; wave++) {
  const before = end.hp;
  const purchased = await post(open+'/move', {key:invader.key,wave,action:'swarm',cell:end.paths[0][0]});
  await post(open+'/move', {key:undefended.key,wave,action:'ready'});
  end = await post(open+'/move', {key:invader.key,wave,action:'ready'});
  const nextDefender = await post(open+'/state', {key:undefended.key});
  const income = [nextDefender.gold-defender.gold, end.gold-purchased.gold];
  if (end.done) assert.deepEqual(income,[0,0]);
  else if (previousIncome) assert.deepEqual(income, previousIncome.map(g=>2*g));
  else assert.ok(income.every(g=>g>0));
  previousIncome = income;
  defender = nextDefender;
  assert.ok(end.hp < before);
  assert.ok(end.frames.some((f,i)=>i && f.hp < end.frames[i-1].hp));
}
assert.equal(end.done,true);
assert.equal(end.hp,0);
await post(open+'/move', {key:invader.key,wave:end.last+1,action:'swarm',cell:end.paths[0][0]}, false);
const restarted = await post(open+'/move', {key:invader.key,wave:end.last+1,action:'rematch'});
assert.equal(restarted.last,0,'a rematch starts from the first wave');
assert.equal(restarted.done,false);
assert.ok(restarted.hp > 0);
console.log('PASS: a finished room answers for a rematch');
console.log('PASS: shared random maps, roles, budget, placement, privacy, synchronized waves, damage, and end lock');

const works = async (build) => {
  const host = await post('/room'), room = `/room/${host.id}`;
  const guest = await post(room + '/join');
  const view = await post(room + '/state', {key: host.key});
  return {room, host, guest, view};
};
for (const [type, price] of Object.entries({cannon: 8, frost: 6, spikes: 3})) {
  const {room, host, view} = await works();
  const road = new Set([...view.paths, ...view.alternatePaths].flat()), w = view.width;
  // Spikes are a trap on the road, while the other two stand on a wall beside it
  const spot = type === 'spikes' ? view.fenceCells[0] : [...Array(w * view.height).keys()].find(c => !road.has(c) &&
    (road.has(c - 1) || road.has(c + 1) || road.has(c - w) || road.has(c + w)));
  const built = await post(room + '/move', {key: host.key, wave: 1, action: type, cell: spot});
  assert.equal(built.gold, view.gold - price, type + ' costs its price');
  assert.ok(built.towers.some(t => t.type === type && t.cell === spot), type + ' stands where it was placed');
}
{
  const {room, host, view} = await works();
  await post(room + '/move', {key: host.key, wave: 1, action: 'spikes', cell: view.paths[0][0]}, false);
  await post(room + '/move', {key: host.key, wave: 1, action: 'spikes', cell: view.paths[0].at(-1)}, false);
}
for (const [type, count] of Object.entries({runner: 6, armored: 3, decoy: 4})) {
  const {room, host, guest, view} = await works();
  await post(room + '/move', {key: guest.key, wave: 1, action: type, cell: view.paths[0][0]});
  await post(room + '/move', {key: host.key, wave: 1, action: 'ready'});
  const wave = await post(room + '/move', {key: guest.key, wave: 1, action: 'ready'});
  const kinds = wave.frames.flatMap(f => f.mobs.map(m => m.kind));
  assert.equal(kinds.length >= count, true, type + ' sends at least ' + count);
  assert.ok(kinds.every(kind => kind === type), type + ' sends only itself');
}
const decoyRoom = await works();
const decoyGate = decoyRoom.view.paths[0][0];
const options = [decoyRoom.view.paths[0], ...decoyRoom.view.alternatePaths.filter(p => p[0] === decoyGate)];
assert.ok(options.length > 1, 'the gate forks, so the decoy has a long way to take');
console.log('PASS: every new structure is buildable and every new slime spawns only itself');
{
  const {room, host, guest, view} = await works();
  const w = view.width, trap = view.paths[0][2];
  await post(room + '/move', {key: host.key, wave: 1, action: 'spikes', cell: trap});
  await post(room + '/move', {key: guest.key, wave: 1, action: 'swarm', cell: view.paths[0][0]});
  await post(room + '/move', {key: host.key, wave: 1, action: 'ready'});
  const wave = await post(room + '/move', {key: guest.key, wave: 1, action: 'ready'});
  const [tx, ty] = [trap % w, Math.floor(trap / w)];
  const bites = wave.frames.flatMap(f => f.shots.filter(s => s.cell === trap));
  assert.ok(bites.length > 0, 'the spikes bite at least once');
  assert.ok(bites.every(s => s.x === tx && s.y === ty), 'the spikes bite only the slime on their own cell');
}
{
  const {room, host, guest, view} = await works();
  const w = view.width, road = new Set(view.paths[0]);
  const beside = view.paths[0].slice(2, -2).flatMap(cell => [cell - 1, cell + 1, cell - w, cell + w])
    .find(cell => cell >= 0 && cell < w * view.height && !road.has(cell));
  await post(room + '/move', {key: host.key, wave: 1, action: 'frost', cell: beside});
  await post(room + '/move', {key: guest.key, wave: 1, action: 'swarm', cell: view.paths[0][0]});
  await post(room + '/move', {key: host.key, wave: 1, action: 'ready'});
  const wave = await post(room + '/move', {key: guest.key, wave: 1, action: 'ready'});
  const [fx, fy] = [beside % w, Math.floor(beside / w)];
  const frost = wave.frames.flatMap(f => f.shots.filter(s => s.cell === beside));
  assert.ok(frost.length > 0, 'the frost reaches the road beside it');
  assert.ok(frost.every(s => Math.abs(s.x - fx) <= 1 && Math.abs(s.y - fy) <= 1), 'the frost freezes only its 3×3 square');
}
console.log('PASS: the spikes bite one cell and the frost freezes a 3×3 square');
{
  const {room, host, guest, view} = await works();
  const guestView = await post(room + '/state', {key: guest.key});
  const bought = await post(room + '/move', {key: guest.key, wave: 1, action: 'swarm', cell: view.paths[0][0]});
  assert.equal(bought.orders.length, 1);
  const cancelled = await post(room + '/move', {key: guest.key, wave: 1, action: 'cancel', cell: 0});
  assert.equal(cancelled.orders.length, 0, 'the order is gone');
  assert.equal(cancelled.gold, guestView.gold, 'the gold comes back');
  await post(room + '/move', {key: host.key, wave: 1, action: 'cancel', cell: 0}, false);
}
{
  const {room, host, view} = await works();
  const route = view.paths[0], w = view.width, road = new Set([...view.paths, ...view.alternatePaths].flat());
  const wall = route.slice(2, -3).flatMap(cell => [cell - 1, cell + 1, cell - w, cell + w])
    .find(cell => cell >= 0 && cell < w * view.height && !road.has(cell));
  const first = await post(room + '/move', {key: host.key, wave: 1, action: 'spikes', cell: route[3]});
  assert.ok(first.towers.some(t => t.type === 'spikes'));
  await post(room + '/move', {key: host.key, wave: 1, action: 'spikes', cell: route[5]}, false);
  assert.ok(wall >= 0, 'the map offers a wall for a second trap test');
}
{
  const {room, host, guest} = await works();
  const swapped = await post(room + '/move', {key: host.key, wave: 1, action: 'swap'});
  assert.equal(swapped.seat, 1, 'the player who defended now attacks');
  assert.equal((await post(room + '/state', {key: guest.key})).seat, 0, 'and the other way round');
}
{
  const {room, host, view} = await works();
  const road = new Set([...view.paths, ...view.alternatePaths].flat()), w = view.width;
  const wall = [...Array(w * view.height).keys()].find(cell => !road.has(cell) &&
    (road.has(cell - 1) || road.has(cell + 1) || road.has(cell - w) || road.has(cell + w)));
  await post(room + '/move', {key: host.key, wave: 1, action: 'bolt', cell: wall});
  await post(room + '/move', {key: host.key, wave: 1, action: 'swap'}, false);
}
console.log('PASS: a landing can be cancelled, one trap per road, and the sides can change');
{
  const {room, host, guest, view} = await works();
  assert.equal(view.prices.smart, 5, 'the smart slimes cost five');
  const road = new Set([...view.paths, ...view.alternatePaths].flat()), w = view.width;
  const wall = view.paths[0].slice(1, -1).flatMap(cell => [cell - 1, cell + 1, cell - w, cell + w])
    .find(cell => cell >= 0 && cell < w * view.height && !road.has(cell));
  await post(room + '/move', {key: host.key, wave: 1, action: 'bolt', cell: wall});
  await post(room + '/move', {key: guest.key, wave: 1, action: 'ranged', cell: view.paths[0][0]});
  await post(room + '/move', {key: host.key, wave: 1, action: 'ready'});
  const wave = await post(room + '/move', {key: guest.key, wave: 1, action: 'ready'});
  const hits = wave.frames.flatMap(f => f.siege || []).filter(s => s.cell === wall);
  assert.ok(hits.length > 0, 'the ranged slime hits the tower it can see');
  const damaged = !wave.towers.some(t => t.cell === wall) || wave.towers.some(t => t.cell === wall && t.hp < t.max);
  assert.ok(damaged, 'the tower loses health or falls');
}
{
  const {room, host, guest, view} = await works();
  await post(room + '/move', {key: guest.key, wave: 1, action: 'apc', cell: view.paths[0][0]});
  await post(room + '/move', {key: host.key, wave: 1, action: 'ready'});
  const wave = await post(room + '/move', {key: guest.key, wave: 1, action: 'ready'});
  const kinds = new Set(wave.frames.flatMap(f => f.mobs.map(m => m.kind)));
  assert.ok(kinds.has('apc'), 'the transport drives in');
  assert.ok(kinds.has('balaclava'), 'and lets the passengers out halfway');
}
console.log('PASS: the ranged slime breaks towers and the transport lands balaclavas');
