const base = process.env.URL || 'http://localhost:5184';
const samples = Number(process.env.SAMPLES || 12);
async function post(path, data={}) {
  const r = await fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
  if (!r.ok) throw Error(`${path} ${JSON.stringify(data)}: ${r.status}`);
  return r.json();
}
async function match(defense, attack, n) {
  const d = await post('/room'), room = `/room/${d.id}`, a = await post(room+'/join');
  const view = p => post(room+'/state',{key:p.key});
  const buy = (p,wave,action,cell) => post(room+'/move',{key:p.key,wave,action,cell});
  let ds = await view(d), as = await view(a);
  const paths = [...ds.paths,...ds.alternatePaths], road = new Set(paths.flat()), keep = ds.paths[0].at(-1);
  const dx = (u,v) => u%ds.width-v%ds.width, dy = (u,v) => Math.floor(u/ds.width)-Math.floor(v/ds.width);
  const dist = (u,v) => dx(u,v)**2+dy(u,v)**2;
  const walls = Array.from({length:ds.width*ds.height},(_,i)=>i).filter(i=>!road.has(i)&&[...road].some(j=>dist(i,j)===1));
  for(let wave=1;!ds.done;wave++) {
    if (defense==='keep'||defense==='spread') {
      const types = defense==='keep' ? ['bolt'] : ['pulse','bolt'];
      while (true) {
        const type=types.find(t=>ds.gold>=ds.prices[t]); if(!type)break;
        const spots=walls.filter(i=>!ds.towers.some(t=>t.cell===i)); if(!spots.length)break;
        const score=i=>defense==='keep' ? -dist(i,keep) : paths.reduce((total,p)=>total+p.reduce((sum,j)=>sum+(dist(i,j)<=(type==='bolt'?9:4)? 1/(1+ds.towers.filter(t=>dist(t.cell,j)<=(t.type==='bolt'?9:4)).length):0),0),0);
        spots.sort((u,v)=>score(v)-score(u)); ds=await buy(d,wave,type,spots[0]);
      }
    }
    const costs=as.prices;
    for(let i=0;as.gold>=costs.swarm;i++) {
      const type=attack==='giant'&&as.gold>=costs.giant?'giant':attack==='smart'&&costs.smart&&as.gold>=costs.smart?'smart':'swarm';
      const weigh=p=>p.reduce((sum,c)=>sum+ds.towers.reduce((v,t)=>v+(dist(t.cell,c)<=(t.type==='bolt'?9:4)?1:0),0),0);
      const routes=[...as.paths].sort((u,v)=>weigh(u)-weigh(v));
      const route=attack==='swarm'?routes[(n+i)%routes.length]:routes[0];
      as=await buy(a,wave,type,route[0]);
    }
    await buy(d,wave,'ready'); as=await buy(a,wave,'ready'); ds=await view(d);
  }
  return [ds.hp,ds.last];
}
for(const defense of ['keep','spread']) for(const attack of ['swarm','giant','smart']) {
  const results=[];
  for(let i=0;i<samples;i++) results.push(await match(defense,attack,i));
  const wins=results.filter(([hp])=>hp===0).length;
  const hp=results.reduce((sum,[hp])=>sum+hp,0)/samples;
  const waves=results.reduce((sum,[,wave])=>sum+wave,0)/samples;
  console.log(`${defense} vs ${attack}: attack wins ${wins}/${samples}, mean HP ${hp.toFixed(1)}, mean waves ${waves.toFixed(1)}`);
}
