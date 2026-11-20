using System.Collections.Immutable;

internal sealed class Duel {
    const int Defender = 0, Attacker = 1, WaveCount = 5;
    const int FenceHealth = 12;
    static readonly PriceList Prices = new(Bolt: 5, Pulse: 6, Swarm: 4, Giant: 7, Fence: 4);
    readonly record struct PriceList(int Bolt, int Pulse, int Swarm, int Giant, int Fence) {
        public int Smart => 2 * Swarm;
    }
    readonly record struct Purchase(string Type, int Cell);
    readonly record struct Mob(int X, int Y, bool Giant, bool Smart);
    readonly record struct Fence(int Cell, int Hp);
    readonly record struct Target(int Cell, int X, int Y);
    readonly record struct Frame(int Hp, ImmutableArray<Mob> Mobs, ImmutableArray<Target> Shots,
        ImmutableArray<Target> Aims, ImmutableArray<Fence> Fences);

    readonly string[] keys = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
    readonly Maze maze = new();
    readonly bool[] ready = new bool[2];
    readonly int[] gold = [10, 8];
    readonly List<Purchase> towers = [];
    readonly Dictionary<int, int> fences = [];
    readonly List<Purchase> orders = [];
    ImmutableArray<Frame> frames = [];
    int hp = 24;
    int last;

    public sealed record WaveWrite(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled, int Groups);
    public sealed record ResultWrite(Guid Id, DateTime StartedAt, DateTime FinishedAt, int WavesPlayed,
        int HeartsLeft, string Winner, bool Survivor, WaveWrite[] Log);

    readonly Guid matchId = Guid.NewGuid();
    readonly DateTime startedAt = DateTime.UtcNow;
    readonly List<WaveWrite> log = [];
    ResultWrite? result;

    public ResultWrite? TakeResult() {
        var finished = result;
        result = null;
        return finished;
    }

    public bool Joined { get; private set; }
    bool Done => last >= WaveCount || hp == 0;
    public string Key(int seat) => keys[seat];
    public int Seat(string? key) => Array.IndexOf(keys, key);
    public bool Join() {
        if (Joined) return false;
        Joined = true;
        return true;
    }

    public object View(int seat) => new {
        joined = Joined, last, done = Done, locked = ready[seat], gold = gold[seat], hp,
        width = Maze.Width, height = Maze.Height,
        paths = maze.Routes.Values.ToArray(),
        alternatePaths = maze.RouteOptions.Values.SelectMany(options => options.Skip(1)).ToArray(),
        fenceCells = maze.FenceCells,
        fences = FenceSnapshot(),
        towers = towers.ToArray(),
        orders = seat == Attacker ? orders.ToArray() : [],
        prices = Prices, frames
    };

    public IResult? Apply(int seat, Move move) {
        if (!Joined || Done || move.Wave != last + 1 || ready[seat]) return Results.Conflict();
        if (move.Action == "ready") {
            if (seat == Attacker && orders.Count == 0) return Results.Conflict();
            ready[seat] = true;
            if (ready[Defender] && ready[Attacker]) Resolve();
            return null;
        }

        int price = move.Action switch {
            "bolt" => Prices.Bolt,
            "pulse" => Prices.Pulse,
            "fence" => Prices.Fence,
            "swarm" => Prices.Swarm,
            "smart" => Prices.Smart,
            "giant" => Prices.Giant,
            _ => 0
        };
        bool fence = move.Action == "fence";
        bool tower = move.Action is "bolt" or "pulse";
        if (price == 0 || (fence || tower) != (seat == Defender) || move.Cell is not int cell)
            return Results.BadRequest();
        bool valid = fence ? maze.CanFence(cell) : tower ? maze.CanBuild(cell) : maze.Routes.ContainsKey(cell);
        if (!valid) return Results.BadRequest();
        if (price > gold[seat] || (fence && fences.ContainsKey(cell)) || (tower && towers.Exists(t => t.Cell == cell)))
            return Results.Conflict();

        gold[seat] -= price;
        if (fence) fences.Add(cell, FenceHealth);
        else (tower ? towers : orders).Add(new(move.Action!, cell));
        return null;
    }

    void Resolve() {
        last++;
        int before = hp, groups = orders.Count;
        var (replay, spawned, reached) = Fight();
        frames = replay;
        log.Add(new WaveWrite(last, hp, before - hp, spawned, spawned - reached, groups));
        orders.Clear();
        Array.Clear(ready);
        if (Done) result = new ResultWrite(matchId, startedAt, DateTime.UtcNow, last, hp,
            hp == 0 ? "Attacker" : "Defender", hp > 0, log.ToArray());
        else {
            int multiplier = 1 << (last - 1);
            gold[Defender] += 3 * multiplier;
            gold[Attacker] += 6 * multiplier;
        }
    }

    sealed class Slime(int[] path, int health, bool giant, bool smart) {
        int index;
        int nextStep;
        int StepInterval => Giant ? 3 : 2;
        public int Health = health;
        public int Cell => path[index];
        public void Spawn(int tick) => nextStep = tick + StepInterval;
        public bool Giant { get; } = giant;
        public bool Smart { get; } = smart;
        public int Remaining => path.Length - 1 - index;
        public int X => path[index] % Maze.Width;
        public int Y => path[index] / Maze.Width;

        public void Advance(int tick, Dictionary<int, int> fences) {
            if (tick < nextStep || Remaining == 0) return;
            nextStep = tick + StepInterval;
            int nextCell = path[index + 1];
            if (fences.TryGetValue(nextCell, out int health)) {
                int left = health - (Giant ? 2 : 1);
                if (left > 0) fences[nextCell] = left;
                else fences.Remove(nextCell);
                return;
            }
            index++;
        }
    }

    ImmutableArray<Fence> FenceSnapshot() => fences.Select(f => new Fence(f.Key, f.Value)).ToImmutableArray();

    (ImmutableArray<Frame> Frames, int Spawned, int Reached) Fight() {
        Dictionary<int, Queue<Slime>> pending = [];
        foreach (var order in orders) {
            bool smart = order.Type == "smart", giant = order.Type == "giant";
            if (!pending.TryGetValue(order.Cell, out var queue)) pending.Add(order.Cell, queue = new());
            for (int n = 0; n < (giant ? 1 : 5); n++) {
                var options = maze.RouteOptions[order.Cell];
                var path = options[smart ? 0 : Random.Shared.Next(options.Length)];
                queue.Enqueue(new(path, giant ? 26 + 2 * last : 2 + last, giant, smart));
            }
        }

        List<Slime> mobs = [];
        int spawned = 0, reached = 0;
        var replay = ImmutableArray.CreateBuilder<Frame>();
        for (int tick = 0; hp > 0 && (pending.Values.Any(queue => queue.Count > 0) || mobs.Count > 0); tick++) {
            foreach (var mob in mobs) mob.Advance(tick, fences);
            foreach (var (gate, queue) in pending) {
                if (queue.Count == 0 || mobs.Any(mob => mob.Cell == gate)) continue;
                var next = queue.Dequeue();
                next.Spawn(tick);
                mobs.Add(next);
                spawned++;
            }

            var shots = ImmutableArray.CreateBuilder<Target>();
            var aims = ImmutableArray.CreateBuilder<Target>();
            foreach (var tower in towers) {
                bool bolt = tower.Type == "bolt";
                int x = tower.Cell % Maze.Width, y = tower.Cell / Maze.Width;
                int rangeSquared = bolt ? 9 : 4;
                var targets = mobs.Where(m => m.Health > 0 && Square(m.X - x) + Square(m.Y - y) <= rangeSquared);
                if (targets.MinBy(m => m.Remaining) is not {} lead) continue;
                aims.Add(new(tower.Cell, lead.X, lead.Y));
                if (tick % (bolt ? 4 : 3) != 0) continue;
                if (bolt) Hit(lead, 4);
                else foreach (var mob in targets) Hit(mob, 1);

                void Hit(Slime mob, int damage) {
                    mob.Health -= damage;
                    shots.Add(new(tower.Cell, mob.X, mob.Y));
                }
            }

            foreach (var mob in mobs)
                if (mob.Health > 0 && mob.Remaining == 0) {
                    hp = Math.Max(0, hp - (mob.Giant ? 5 : 1));
                    reached++;
                }
            mobs.RemoveAll(m => m.Health <= 0 || m.Remaining == 0);
            replay.Add(new(hp, mobs.Select(m => new Mob(m.X, m.Y, m.Giant, m.Smart)).ToImmutableArray(),
                shots.ToImmutable(), aims.ToImmutable(), FenceSnapshot()));
        }

        return (replay.ToImmutable(), spawned, reached);
    }

    static int Square(int x) => x * x;
}
