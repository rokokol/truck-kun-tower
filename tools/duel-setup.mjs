// The pictures and the demo want a board that is not empty, and a wave alone cannot pay for one:
// a wave pays 11, 12, 14, 16 and 18 for the defender and the gold does not carry over. The five
// waves together can, so both tools spend the first four here and photograph the last one
export const LAST_WAVE = 5;

// Towers go on walls beside the roads, traps on the road itself, and the order matters because a
// wave buys from the top of the list until its own gold runs out
const DEFENCE = [
  ['bolt', 'wall'], ['pulse', 'wall'], ['frost', 'wall'], ['fence', 'road'],
  ['cannon', 'wall'], ['bolt', 'wall'], ['pulse', 'wall'], ['frost', 'wall'],
  ['spikes', 'road'], ['fence', 'road'], ['spikes', 'road'],
];

// The ways to the keep, taken from the middle of each road to its last step, so the towers guard
// the roads themselves rather than the gates. A path's walls are walked in order, then the paths
// take turns, so one road does not take every tower
function ways(state) {
  const road = new Set([...state.paths, ...state.alternatePaths].flat());
  const {width, height} = state;
  const beside = cell => {
    const x = cell % width;
    const y = (cell - x) / width;
    return [[0, -1], [0, 1], [-1, 0], [1, 0]]
      .map(([dx, dy]) => [x + dx, y + dy])
      .filter(([nx, ny]) => nx >= 0 && ny >= 0 && nx < width && ny < height)
      .map(([nx, ny]) => ny * width + nx)
      .filter(wall => !road.has(wall));
  };
  const walls = state.paths.map(path => path.slice(Math.floor(path.length / 2)).flatMap(beside)
    .filter((cell, index, all) => all.indexOf(cell) === index));
  const traps = state.paths.map(path => path.slice(-4, -1));
  const interleave = lists => {
    const cells = [];
    for (let index = 0; lists.some(list => list[index] !== undefined); index += 1) {
      for (const list of lists) if (list[index] !== undefined) cells.push(list[index]);
    }
    return cells;
  };
  return {walls: interleave(walls), traps: interleave([...traps, state.fenceCells])};
}

// A wave with no order closes before the reply arrives, so four quiet waves cost eight requests.
// The plan is tried slot by slot every wave: a slot that the wave cannot pay for stays for the
// next one, and a cell the board refuses is passed over rather than ending the wave
export async function fillDefence(base, room, hostSeat, visitorSeat, state) {
  const {walls, traps} = ways(state);
  let wall = 0;
  let trap = 0;
  let bought = 0;
  for (let wave = 1; wave < LAST_WAVE; wave += 1) {
    for (let slot = bought; slot < DEFENCE.length; slot += 1) {
      const [action, where] = DEFENCE[slot];
      const cell = where === 'wall' ? walls[wall] : traps[trap];
      if (cell === undefined) continue;
      const response = await fetch(`${base}${room}/moves`, {
        method: 'POST',
        signal: AbortSignal.timeout(15000),
        headers: {'Content-Type': 'application/json', 'X-Seat-Token': hostSeat},
        body: JSON.stringify({wave, action, cell}),
      });
      await response.text();
      if (where === 'wall') wall += 1;
      else trap += 1;
      if (!response.ok) continue;
      bought = slot + 1;
    }
    for (const seat of [visitorSeat, hostSeat]) {
      const response = await fetch(`${base}${room}/moves`, {
        method: 'POST',
        signal: AbortSignal.timeout(15000),
        headers: {'Content-Type': 'application/json', 'X-Seat-Token': seat},
        body: JSON.stringify({wave, action: 'ready', cell: null}),
      });
      if (!response.ok) throw new Error(`wave ${wave} did not close: ${await response.text()}`);
      await response.text();
    }
  }
  if (bought < 6) throw new Error(`five waves paid for only ${bought} defensive orders`);
  return bought;
}
