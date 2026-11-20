import assert from 'node:assert/strict';
const base = process.env.URL || 'http://localhost:5184';
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
assert.equal(fencedState.prices.smart,2*fencedState.prices.swarm);
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
    assert.ok(mob.smart);
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
console.log('PASS: shared random maps, roles, budget, placement, privacy, synchronized waves, damage, and end lock');
