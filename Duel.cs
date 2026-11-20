using System.Collections.Immutable;
using System.Security.Cryptography;

public sealed class Duel {
    public const int Defender = 0, Attacker = 1;
    const int WaveCount = 5, ThinkTime = 30;
    const int FenceHealth = 12;
    static readonly PriceList Prices = new(Bolt: 5, Pulse: 6, Cannon: 8, Frost: 6, Spikes: 3,
        Swarm: 4, Giant: 7, Runner: 3, Armored: 8, Decoy: 5, Ranged: 10, Apc: 12, Fence: 4);
    readonly record struct PriceList(int Bolt, int Pulse, int Cannon, int Frost, int Spikes,
        int Swarm, int Giant, int Runner, int Armored, int Decoy, int Ranged, int Apc, int Fence) {
        public int Smart => 5;
    }

    // The ranged slime shoots buildings, so a tower now has health of its own
    sealed class Building(string type, int cell, int hp) {
        public string Type { get; } = type;
        public int Cell { get; } = cell;
        public int Hp = hp;
        public int Max { get; } = hp;
    }
    readonly record struct Purchase(string Type, int Cell);
    readonly record struct Mob(int X, int Y, string Kind, bool Frozen);
    readonly record struct Fence(int Cell, int Hp);
    readonly record struct BuildingView(string Type, int Cell, int Hp, int Max);
    readonly record struct Target(int Cell, int X, int Y);
    readonly record struct Frame(int Hp, ImmutableArray<Mob> Mobs, ImmutableArray<Target> Shots,
        ImmutableArray<Target> Aims, ImmutableArray<Fence> Fences, ImmutableArray<BuildingView> Buildings,
        ImmutableArray<Target> Siege);

    // A chair belongs to an account, or to a guest holding the random token from the join.
    // A sideshow of keys used to be the whole identity; now it is only the guest's half of it.
    public sealed class Seat {
        public Guid? UserId { get; init; }
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }
    readonly Seat?[] seats = new Seat?[2];
    int? swapBy;
    Maze maze = new();
    readonly bool[] ready = new bool[2];
    readonly int[] gold = [10, 16];
    readonly List<Building> towers = [];
    readonly Dictionary<int, int> fences = [];
    readonly List<Purchase> orders = [];
    ImmutableArray<Frame> frames = [];
    int hp = 24;
    int last;

    public sealed record WaveWrite(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled, int Groups);
    public sealed record ResultWrite(Guid Id, Guid? DefenderId, Guid? AttackerId,
        DateTime StartedAt, DateTime FinishedAt, int WavesPlayed,
        int HeartsLeft, string Winner, bool Survivor, WaveWrite[] Log);

    Guid matchId = Guid.NewGuid();
    DateTime startedAt = DateTime.UtcNow;
    DateTime deadline = DateTime.UtcNow.AddSeconds(ThinkTime);
    readonly List<WaveWrite> log = [];
    ResultWrite? result;

    public ResultWrite? TakeResult() {
        var finished = result;
        result = null;
        return finished;
    }

    public bool Joined => seats[Defender] is not null && seats[Attacker] is not null;
    public bool IsFull => Joined;
    public int Occupied => (seats[Defender] is null ? 0 : 1) + (seats[Attacker] is null ? 0 : 1);
    bool Done => last >= WaveCount || hp == 0;
    public bool Expired => !Done && DateTime.UtcNow >= deadline;
    int SecondsLeft => Done ? 0 : Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
    public Guid? UserAt(int side) => seats[side]?.UserId;
    public Seat? SeatAt(int side) => seats[side];

    public int SeatOf(PlayerRef who) {
        for (var side = 0; side < 2; side++) {
            var seat = seats[side];
            if (seat is null) continue;
            if (seat.UserId is { } id) {
                if (who.UserId == id) return side;
            } else if (who.SeatToken is { } token && TokensMatch(seat.Token, token)) return side;
        }
        return -1;
    }

    static bool TokensMatch(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));

    // Taking a chair is the join: the first registered account or guest gets the side they picked
    public bool TryTake(PlayerRef who, int side, out Seat seat) {
        seat = null!;
        if (side is not (Defender or Attacker) || seats[side] is not null) return false;
        if (who.UserId is { } id && (seats[Defender]?.UserId == id || seats[Attacker]?.UserId == id)) return false;
        seat = seats[side] = new Seat { UserId = who.UserId };
        // The clock starts when the pair is complete, so the second player is not already late
        if (Joined) deadline = DateTime.UtcNow.AddSeconds(ThinkTime);
        return true;
    }

    // Nothing built, nothing queued, first wave still ahead: sides may move
    bool Untouched => last == 0 && towers.Count == 0 && fences.Count == 0 && orders.Count == 0 &&
        !ready[Defender] && !ready[Attacker];

    public bool CanSwitch => Untouched && Occupied > 0;
    public int? SwapRequestedBy => swapBy;

    // Two players: asking to switch puts the question to the other chair. Alone: just move.
    public bool AskSwitch(int side) {
        if (!CanSwitch) return false;
        if (Occupied == 1) {
            if (seats[side] is null || seats[1 - side] is not null) return false;
            seats[1 - side] = seats[side];
            seats[side] = null;
            return true;
        }
        if (swapBy is not null) return false;
        swapBy = side;
        return true;
    }

    public bool AnswerSwitch(int side, bool accept) {
        if (swapBy is not int asked || asked == side) return false;
        // The question is over either way; a side that has been built on meanwhile is not swapped
        swapBy = null;
        if (accept && CanSwitch) (seats[Defender], seats[Attacker]) = (seats[Attacker], seats[Defender]);
        return true;
    }

    public object View(int seat, string? opponent = null) => new {
        joined = Joined, seat, side = Sides.Name(seat), last, done = Done, locked = ready[seat],
        gold = gold[seat], hp,
        deadline, secondsLeft = SecondsLeft,
        swap = swapBy is null ? null : (swapBy == seat ? "mine" : "theirs"),
        opponent, opponentId = UserAt(1 - seat),
        width = Maze.Width, height = Maze.Height,
        paths = maze.Routes.Values.ToArray(),
        alternatePaths = maze.RouteOptions.Values.SelectMany(options => options.Skip(1)).ToArray(),
        fenceCells = maze.FenceCells,
        fences = FenceSnapshot(),
        towers = buildingsNow(),
        orders = seat == Attacker ? orders.ToArray() : [],
        prices = Prices, frames
    };

    public IResult? Apply(int seat, Move move) {
        // A finished game still answers for a rematch, so this check comes before the general guard
        if (move.Action == "rematch") {
            if (!Done) return Results.Conflict();
            Reset();
            return null;
        }
        if (!Joined || Done || Expired || move.Wave != last + 1 || ready[seat]) return Results.Conflict();
        if (move.Action == "cancel") {
            if (seat != Attacker || move.Cell is not int index || index < 0 || index >= orders.Count)
                return Results.BadRequest();
            gold[Attacker] += PriceOf(orders[index].Type)!.Value;
            orders.RemoveAt(index);
            return null;
        }
        if (move.Action == "ready") {
            if (seat == Attacker && orders.Count == 0) return Results.Conflict();
            ready[seat] = true;
            if (ready[Defender] && ready[Attacker]) Resolve();
            return null;
        }

        int price = PriceOf(move.Action) ?? 0;
        bool fence = move.Action == "fence";
        bool trap = move.Action == "spikes";
        bool tower = move.Action is "bolt" or "pulse" or "cannon" or "frost" or "spikes";
        if (price == 0 || (fence || tower) != (seat == Defender) || move.Cell is not int cell)
            return Results.BadRequest();
        // The spikes lie on the road, like a fence, instead of standing on a wall
        bool valid = fence || trap ? maze.CanFence(cell) : tower ? maze.CanBuild(cell) : maze.Routes.ContainsKey(cell);
        if (!valid) return Results.BadRequest();
        // Two fences on one cell would be paid for and then thrown away as a duplicate key
        if (price > gold[seat] || ((fence || trap) && fences.ContainsKey(cell)) ||
            (tower && towers.Exists(t => t.Cell == cell)))
            return Results.Conflict();
        // One trap per road: two sets of spikes on the same path would stop the wave alone
        if (trap && towers.Where(t => t.Type == "spikes").Any(t => SharesRoute(t.Cell, cell)))
            return Results.Conflict();

        gold[seat] -= price;
        if (fence) fences.Add(cell, FenceHealth);
        else if (tower) towers.Add(new Building(move.Action!, cell, TowerHealth(move.Action!)));
        else orders.Add(new(move.Action!, cell));
        return null;
    }

    bool SharesRoute(int a, int b) => maze.RouteOptions.Values
        .Any(options => options.Any(path => path.Contains(a) && path.Contains(b)));

    void Reset() {
        maze = new Maze();
        gold[Defender] = 10;
        gold[Attacker] = 16;
        towers.Clear();
        fences.Clear();
        orders.Clear();
        Array.Clear(ready);
        frames = [];
        log.Clear();
        swapBy = null;
        hp = 24;
        last = 0;
        result = null;
        matchId = Guid.NewGuid();
        startedAt = DateTime.UtcNow;
        deadline = DateTime.UtcNow.AddSeconds(ThinkTime);
    }

    static int? PriceOf(string? action) => action switch {
        "bolt" => Prices.Bolt,
        "pulse" => Prices.Pulse,
        "cannon" => Prices.Cannon,
        "frost" => Prices.Frost,
        "spikes" => Prices.Spikes,
        "fence" => Prices.Fence,
        "swarm" => Prices.Swarm,
        "smart" => Prices.Smart,
        "giant" => Prices.Giant,
        "runner" => Prices.Runner,
        "armored" => Prices.Armored,
        "decoy" => Prices.Decoy,
        "ranged" => Prices.Ranged,
        "apc" => Prices.Apc,
        _ => null
    };

    // The clock runs the wave down for a player who stopped answering
    public bool CloseIfExpired() {
        if (!Joined || !Expired) return false;
        ready[Defender] = true;
        ready[Attacker] = true;
        Resolve();
        return true;
    }

    void Resolve() {
        last++;
        int before = hp, groups = orders.Count;
        var (replay, spawned, reached) = Fight();
        frames = replay;
        log.Add(new WaveWrite(last, hp, before - hp, spawned, spawned - reached, groups));
        orders.Clear();
        Array.Clear(ready);
        if (Done) result = new ResultWrite(matchId, UserAt(Defender), UserAt(Attacker),
            startedAt, DateTime.UtcNow, last, hp,
            hp == 0 ? "Attacker" : "Defender", hp > 0, log.ToArray());
        else {
            int multiplier = 1 << (last - 1);
            gold[Defender] += 3 * multiplier;
            gold[Attacker] += 6 * multiplier;
            deadline = DateTime.UtcNow.AddSeconds(ThinkTime);
        }
    }

    sealed class Slime(int[] path, string kind, int health) {
        int index;
        int nextStep;
        bool frozen;
        public int Health = health;
        public string Kind { get; } = kind;
        public int Cell => path[index];
        public int Remaining => path.Length - 1 - index;
        public int X => path[index] % Maze.Width;
        public int Y => path[index] / Maze.Width;
        public int Touch => Kind switch { "giant" => 5, "apc" => 3, _ => 1 };
        public int PathLength => path.Length;
        public bool Split { get; set; }

        // The transport opens up at the halfway mark and lets its passengers out
        public Slime[] Birth(int count, string kind, int health) {
            var born = new Slime[count];
            for (int n = 0; n < count; n++) {
                born[n] = new Slime(path, kind, health);
                born[n].index = index;
            }
            return born;
        }

        int BaseInterval => Kind switch { "runner" => 1, "giant" => 3, "armored" => 4, _ => 2 };
        int FenceHits => Kind is "giant" or "armored" ? 3 : 1;

        public void Spawn(int tick) => nextStep = tick + BaseInterval;
        public void Slow() => frozen = true;
        public bool Frozen => frozen;
        public void Damage(int amount) => Health -= Kind == "armored" ? Math.Max(1, amount / 2) : amount;

        public void Advance(int tick, Dictionary<int, int> fences) {
            if (tick < nextStep || Remaining == 0) return;
            // A frozen slime loses one whole step, which is what the ice on the board shows
            if (frozen) {
                frozen = false;
                nextStep = tick + BaseInterval;
                return;
            }
            nextStep = tick + BaseInterval;
            int nextCell = path[index + 1];
            if (fences.TryGetValue(nextCell, out int health)) {
                int left = health - FenceHits;
                if (left > 0) fences[nextCell] = left;
                else fences.Remove(nextCell);
                return;
            }
            index++;
        }
    }

    static int TowerHealth(string type) => type switch {
        "cannon" => 30,
        "pulse" => 20,
        "frost" => 18,
        "spikes" => 14,
        _ => 24
    };

    ImmutableArray<BuildingView> buildingsNow() => towers
        .Select(t => new BuildingView(t.Type, t.Cell, t.Hp, t.Max)).ToImmutableArray();

    static (int Damage, int Range, int Every, bool HitsAll) Stats(string type) => type switch {
        "bolt" => (4, 9, 4, false),
        "pulse" => (1, 4, 3, true),
        "cannon" => (9, 4, 8, false),
        "frost" => (0, 4, 2, true),
        "spikes" => (2, 0, 2, true),
        _ => (0, 0, 1, false)
    };

    ImmutableArray<Fence> FenceSnapshot() => fences.Select(f => new Fence(f.Key, f.Value)).ToImmutableArray();

    (ImmutableArray<Frame> Frames, int Spawned, int Reached) Fight() {
        Dictionary<int, Queue<Slime>> pending = [];
        foreach (var order in orders) {
            if (!pending.TryGetValue(order.Cell, out var queue)) pending.Add(order.Cell, queue = new());
            var (kind, count) = order.Type switch {
                "giant" => ("giant", 1),
                "smart" => ("smart", 5),
                "runner" => ("runner", 6),
                "armored" => ("armored", 3),
                "decoy" => ("decoy", 4),
                "ranged" => ("ranged", 2),
                "apc" => ("apc", 1),
                _ => ("swarm", 5)
            };
            for (int n = 0; n < count; n++) {
                var options = maze.RouteOptions[order.Cell];
                var path = kind switch {
                    "smart" => options.MinBy(option => option.Length)!,
                    "decoy" => options.MaxBy(option => option.Length)!,
                    _ => options[Random.Shared.Next(options.Length)]
                };
                int health = kind switch {
                    "giant" => 26 + 2 * last,
                    "armored" => 6 + last,
                    "runner" => 1,
                    "decoy" => 3,
                    "ranged" => 8 + last,
                    "apc" => 30 + 2 * last,
                    _ => 2 + last
                };
                queue.Enqueue(new(path, kind, health));
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
            var siege = ImmutableArray.CreateBuilder<Target>();
            foreach (var tower in towers) {
                var (damage, range, every, hitsAll) = Stats(tower.Type);
                int x = tower.Cell % Maze.Width, y = tower.Cell / Maze.Width;
                var inRange = mobs.Where(m => m.Health > 0 && InReach(m)).ToList();
                if (inRange.Count == 0) continue;
                var lead = inRange.MinBy(m => m.Remaining)!;
                aims.Add(new(tower.Cell, lead.X, lead.Y));
                if (tick % every != 0) continue;
                if (tower.Type == "frost") {
                    foreach (var mob in inRange) {
                        mob.Slow();
                        shots.Add(new(tower.Cell, mob.X, mob.Y));
                    }
                } else if (hitsAll) {
                    foreach (var mob in inRange) Strike(mob, damage);
                } else Strike(lead, damage);

                void Strike(Slime mob, int amount) {
                    mob.Damage(amount);
                    shots.Add(new(tower.Cell, mob.X, mob.Y));
                }

                // the spikes bite only the slime standing on them, the frost freezes a 3×3 square
                bool InReach(Slime mob) => tower.Type switch {
                    "spikes" => mob.Cell == tower.Cell,
                    "frost" => Math.Abs(mob.X - x) <= 1 && Math.Abs(mob.Y - y) <= 1,
                    _ => Square(mob.X - x) + Square(mob.Y - y) <= range
                };
            }

            // The ranged slime answers back: it picks the weakest building within three cells
            if (tick % 4 == 0)
                foreach (var gunner in mobs.Where(m => m.Kind == "ranged" && m.Health > 0)) {
                    var mark = towers.Where(t => Math.Abs(t.Cell % Maze.Width - gunner.X) <= 3 &&
                            Math.Abs(t.Cell / Maze.Width - gunner.Y) <= 3)
                        .MinBy(t => t.Hp);
                    if (mark is null) continue;
                    mark.Hp -= 3;
                    siege.Add(new(mark.Cell, gunner.X, gunner.Y));
                }
            towers.RemoveAll(t => t.Hp <= 0);

            // The transport opens at the halfway point; broken earlier, it lands nobody
            var landed = new List<Slime>();
            foreach (var carrier in mobs.Where(m => m.Kind == "apc" && m.Health > 0 && !m.Split &&
                         m.Remaining * 2 <= m.PathLength)) {
                carrier.Split = true;
                landed.AddRange(carrier.Birth(4, "balaclava", 3 + last));
            }
            mobs.AddRange(landed);

            foreach (var mob in mobs)
                if (mob.Health > 0 && mob.Remaining == 0) {
                    hp = Math.Max(0, hp - mob.Touch);
                    reached++;
                }
            mobs.RemoveAll(m => m.Health <= 0 || m.Remaining == 0);
            replay.Add(new(hp, mobs.Select(m => new Mob(m.X, m.Y, m.Kind, m.Frozen)).ToImmutableArray(),
                shots.ToImmutable(), aims.ToImmutable(), FenceSnapshot(), buildingsNow(), siege.ToImmutable()));
        }

        return (replay.ToImmutable(), spawned, reached);
    }

    static int Square(int x) => x * x;
}
