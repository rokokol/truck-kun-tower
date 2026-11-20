using System.Collections.Immutable;
using System.Security.Cryptography;

public sealed class Duel {
    public const int Defender = 0, Attacker = 1;
    const int WaveCount = 5, ThinkTime = 60;
    const int ReplayTickMs = 270, ReplayMaxMs = 52000, FightTickLimit = 2048;
    const int CarrierPassengers = 5, SpikeCharges = 5, SpikeDamage = 4;
    const int PulseTargets = 3;
    const int FenceHealth = 16, KeepHealth = 10;
    const int RangedRadius = 2, RangedAmmo = 10, RangedCooldown = 4;
    const int StructureShotDamage = 4, KeepShotDamage = 1;
    static readonly PriceList Prices = new(Bolt: 5, Pulse: 6, Cannon: 8, Frost: 6, Spikes: 3, Mine: 3,
        Swarm: 4, Giant: 7, Runner: 3, Armored: 8, Ranged: 14, Apc: 12, Fence: 4);
    readonly record struct PriceList(int Bolt, int Pulse, int Cannon, int Frost, int Spikes, int Mine,
        int Swarm, int Giant, int Runner, int Armored, int Ranged, int Apc, int Fence) {
        public int Smart => 5;
    }

    internal const string PurchaseLimitCode = "purchase_limit";
    static readonly ImmutableDictionary<string, int> Limits = new Dictionary<string, int> {
        ["bolt"] = 4, ["pulse"] = 3, ["cannon"] = 2, ["frost"] = 2, ["fence"] = 3,
        ["swarm"] = 4, ["giant"] = 2, ["smart"] = 2, ["runner"] = 3, ["armored"] = 2,
        ["ranged"] = 1, ["apc"] = 1
    }.ToImmutableDictionary();

    // The ranged slime shoots buildings, so a tower now has health of its own
    sealed class Building(string type, int cell, int hp) {
        public string Type { get; } = type;
        public int Cell { get; } = cell;
        public int Hp = hp;
        public int Max { get; } = hp;
    }
    readonly record struct Purchase(string Type, int Cell);
    readonly record struct Mob(int X, int Y, string Kind, bool Frozen, int Id, int Hp, int Max);
    readonly record struct Fence(int Cell, int Hp, int Max);
    readonly record struct BuildingView(string Type, int Cell, int Hp, int Max);
    readonly record struct Target(int Cell, int X, int Y);
    readonly record struct SiegeShot(int Cell, int X, int Y, int MobId);
    readonly record struct SiegeTarget(int Cell, Building? Tower = null);
    readonly record struct Frame(int Hp, ImmutableArray<Mob> Mobs, ImmutableArray<Target> Shots,
        ImmutableArray<Target> Aims, ImmutableArray<Fence> Fences, ImmutableArray<BuildingView> Buildings,
        ImmutableArray<SiegeShot> Siege);

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
    int hp = KeepHealth;
    int last;

    public sealed record WaveWrite(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled, int Groups);
    public sealed record ResultWrite(Guid Id, Guid? DefenderId, Guid? AttackerId,
        DateTime StartedAt, DateTime FinishedAt, int WavesPlayed,
        int HeartsLeft, string Winner, bool Survivor, WaveWrite[] Log);

    Guid matchId = Guid.NewGuid();
    DateTime startedAt = DateTime.UtcNow;
    DateTime? deadline;
    DateTime replayEndsAt;
    int replayMs;
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
    public bool Expired => Joined && !Done && deadline is { } end && DateTime.UtcNow >= end;
    int SecondsLeft => !Done && deadline is { } end
        ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds)) : 0;
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
        if (Joined) {
            startedAt = DateTime.UtcNow;
            deadline = startedAt.AddSeconds(ThinkTime);
        }
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
        gold = gold[seat], hp, maxHp = KeepHealth,
        matchId, deadline, secondsLeft = SecondsLeft, replayMs,
        replayLeftMs = replayMs == 0 ? 0 : (int)Math.Clamp((replayEndsAt - DateTime.UtcNow).TotalMilliseconds, 0, replayMs),
        swap = swapBy is null ? null : (swapBy == seat ? "mine" : "theirs"),
        opponent, opponentId = UserAt(1 - seat),
        width = Maze.Width, height = Maze.Height,
        paths = maze.Routes.Values.ToArray(),
        alternatePaths = maze.RouteOptions.Values.SelectMany(options => options.Skip(1)).ToArray(),
        fenceCells = maze.FenceCells,
        trapCells = seat == Defender ? TrapCells() : [],
        fences = FenceSnapshot(),
        towers = buildingsNow(),
        orders = seat == Attacker ? orders.ToArray() : [],
        prices = Prices, limits = Limits, frames
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
        // A structure bought this turn goes back the same way an order does: the gold returns with it
        if (move.Action == "unbuild") {
            if (seat != Defender || move.Cell is not int site) return Results.BadRequest();
            if (towers.FirstOrDefault(t => t.Cell == site) is { } built) {
                gold[Defender] += PriceOf(built.Type)!.Value;
                towers.Remove(built);
                return null;
            }
            if (fences.Remove(site, out _)) {
                gold[Defender] += Prices.Fence;
                return null;
            }
            return Results.Conflict();
        }
        if (move.Action == "ready") {
            ready[seat] = true;
            if (ready[Defender] && ready[Attacker]) Resolve();
            return null;
        }

        int price = PriceOf(move.Action) ?? 0;
        bool fence = move.Action == "fence";
        bool trap = IsTrap(move.Action);
        bool tower = trap || move.Action is "bolt" or "pulse" or "cannon" or "frost";
        if (price == 0 || (fence || tower) != (seat == Defender) || move.Cell is not int cell)
            return Results.BadRequest();
        // The spikes lie on the road, like a fence, instead of standing on a wall
        bool valid = fence || trap ? maze.CanFence(cell) : tower ? maze.CanBuild(cell) : maze.Routes.ContainsKey(cell);
        if (!valid) return Results.BadRequest();
        if (Limits.TryGetValue(move.Action!, out int limit)) {
            int count = fence ? fences.Count : tower ? towers.Count(t => t.Type == move.Action) :
                orders.Count(o => o.Type == move.Action);
            if (count >= limit) return TypedResults.Conflict(PurchaseLimitCode);
        }
        if (price > gold[seat] || (trap && !TrapCells().Contains(cell)) ||
            (fence && fences.ContainsKey(cell)) || ((fence || tower) && towers.Exists(t => t.Cell == cell)))
            return Results.Conflict();

        gold[seat] -= price;
        if (fence) fences.Add(cell, FenceHealth);
        else if (tower) towers.Add(new Building(move.Action!, cell, TowerHealth(move.Action!)));
        else orders.Add(new(move.Action!, cell));
        return null;
    }

    static bool IsTrap(string? type) => type is "spikes" or "mine";

    int[] TrapCells() {
        var traps = towers.Where(t => IsTrap(t.Type)).Select(t => t.Cell).ToHashSet();
        var blocked = maze.RouteOptions.Values.SelectMany(options => options)
            .Where(path => path.Any(traps.Contains)).SelectMany(path => path).ToHashSet();
        return maze.FenceCells.Where(c => !blocked.Contains(c) && !fences.ContainsKey(c)).ToArray();
    }

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
        hp = KeepHealth;
        last = 0;
        result = null;
        matchId = Guid.NewGuid();
        startedAt = DateTime.UtcNow;
        deadline = startedAt.AddSeconds(ThinkTime);
        replayMs = 0;
    }

    static int? PriceOf(string? action) => action switch {
        "bolt" => Prices.Bolt,
        "pulse" => Prices.Pulse,
        "cannon" => Prices.Cannon,
        "frost" => Prices.Frost,
        "spikes" => Prices.Spikes,
        "mine" => Prices.Mine,
        "fence" => Prices.Fence,
        "swarm" => Prices.Swarm,
        "smart" => Prices.Smart,
        "giant" => Prices.Giant,
        "runner" => Prices.Runner,
        "armored" => Prices.Armored,
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
        var (replay, spawned, killed) = Fight();
        frames = replay;
        var now = DateTime.UtcNow;
        replayMs = Math.Min(frames.Length * ReplayTickMs, ReplayMaxMs);
        replayEndsAt = now.AddMilliseconds(replayMs);
        log.Add(new WaveWrite(last, hp, before - hp, spawned, killed, groups));
        swapBy = null;
        orders.Clear();
        Array.Clear(ready);
        if (Done) result = new ResultWrite(matchId, UserAt(Defender), UserAt(Attacker),
            startedAt, now, last, hp,
            hp == 0 ? "Attacker" : "Defender", hp > 0, log.ToArray());
        else {
            int multiplier = 1 << (last - 1);
            gold[Defender] += 4 * multiplier;
            gold[Attacker] += 6 * multiplier;
            deadline = replayEndsAt.AddSeconds(ThinkTime);
        }
    }

    sealed class Slime(int[] path, string kind, int health) {
        int index;
        int nextStep;
        bool frozen;
        int lastFrozenAt = -1;
        bool stoppedByFence;
        int released;
        Slime? previousPassenger;
        bool leftRoute;
        int shotsLeft = kind == "ranged" ? RangedAmmo : 0;
        int nextShot;
        SiegeTarget? siegeTarget;
        int id;
        readonly int maxHealth = health;
        public int Health = health;
        int KeepCell => path[^1];

        public void AssignId(int waveId) => id = waveId;
        public Mob Snapshot() => new(X, Y, Kind, Frozen, id, Health, maxHealth);
        public string Kind { get; } = kind;
        public int Cell => path[index];
        public int Remaining => path.Length - 1 - index;
        public int X => path[index] % Maze.Width;
        public int Y => path[index] / Maze.Width;
        public bool AtKeep => Kind != "apc" && Remaining == 0;
        bool Unloading => Kind == "apc" && (stoppedByFence || index >= (7 * (path.Length - 1) + 9) / 10);
        public bool Unloaded => released == CarrierPassengers;

        public int ReachKeep() {
            leftRoute = true;
            return Kind switch { "giant" => 4, "balaclava" => 2, _ => 1 };
        }

        public Slime? Release(int tick, int health) {
            if (!Unloading || Unloaded || PassengerAt(index)) return null;
            previousPassenger = new Slime(path, "balaclava", health) {
                index = index,
                previousPassenger = previousPassenger
            };
            previousPassenger.Spawn(tick);
            released++;
            return previousPassenger;
        }

        bool PassengerAt(int position) {
            // A delay farther down the road can close the gap between passengers.
            for (var older = previousPassenger; older is not null; older = older.previousPassenger)
                if (older.Health > 0 && !older.leftRoute && older.index == position) return true;
            return false;
        }

        int BaseInterval => Kind switch {
            "runner" or "balaclava" => 1, "giant" or "ranged" => 3, "armored" or "apc" => 4, _ => 2
        };
        int FenceHits => Kind is "giant" or "armored" ? 3 : 1;

        public void Spawn(int tick) => nextStep = tick + BaseInterval;
        public bool Slow() {
            // Without this guard, a frost tower can freeze the same slime again before it moves.
            if (lastFrozenAt == index) return false;
            lastFrozenAt = index;
            return frozen = true;
        }
        public bool Frozen => frozen;
        public void Damage(int amount) => Health -= Kind == "armored" ? Math.Max(1, amount / 2) : amount;

        int SiegeDistance(int cell) => Math.Max(Math.Abs(cell % Maze.Width - X), Math.Abs(cell / Maze.Width - Y));

        public bool AimSiege(List<Building> towers, Dictionary<int, int> fences, int keepHp) {
            if (shotsLeft == 0 || Health <= 0 || keepHp <= 0) return false;
            if (siegeTarget is { } held && SiegeDistance(held.Cell) <= RangedRadius &&
                (held.Tower is { } heldTower ? heldTower.Hp > 0 && towers.Contains(heldTower) :
                    held.Cell == KeepCell || fences.ContainsKey(held.Cell))) return true;

            siegeTarget = null;
            int bestHp = int.MaxValue, bestDistance = int.MaxValue;
            foreach (var tower in towers) Consider(new(tower.Cell, tower), tower.Hp);
            foreach (var (cell, fenceHp) in fences) Consider(new(cell), fenceHp);
            if (siegeTarget is null && SiegeDistance(KeepCell) <= RangedRadius)
                siegeTarget = new(KeepCell);
            return siegeTarget is not null;

            void Consider(SiegeTarget candidate, int targetHp) {
                int distance = SiegeDistance(candidate.Cell);
                if (targetHp <= 0 || distance > RangedRadius || targetHp > bestHp ||
                    (targetHp == bestHp && distance >= bestDistance)) return;
                siegeTarget = candidate;
                bestHp = targetHp;
                bestDistance = distance;
            }
        }

        public void FireSiege(int tick, List<Building> towers, Dictionary<int, int> fences, ref int keepHp,
            ImmutableArray<SiegeShot>.Builder siege) {
            if (tick < nextShot || !AimSiege(towers, fences, keepHp) || siegeTarget is not { } target) return;
            nextShot = tick + RangedCooldown;
            shotsLeft--;
            if (target.Tower is { } tower) tower.Hp -= StructureShotDamage;
            else if (target.Cell == KeepCell) keepHp = Math.Max(0, keepHp - KeepShotDamage);
            else if (fences[target.Cell] <= StructureShotDamage) fences.Remove(target.Cell);
            else fences[target.Cell] -= StructureShotDamage;
            siege.Add(new(target.Cell, X, Y, id));
        }

        public bool Advance(int tick, Dictionary<int, int> fences) {
            if (tick < nextStep || Remaining == 0 || Unloading) return false;
            nextStep = tick + BaseInterval;
            // A frozen slime loses one whole step, which is what the ice on the board shows
            if (frozen) {
                frozen = false;
                return false;
            }
            if (PassengerAt(index + 1)) return false;
            int nextCell = path[index + 1];
            if (fences.TryGetValue(nextCell, out int health)) {
                if (Kind == "apc") {
                    fences.Remove(nextCell);
                    stoppedByFence = true;
                } else {
                    int left = health - FenceHits;
                    if (left > 0) fences[nextCell] = left;
                    else fences.Remove(nextCell);
                }
                return false;
            }
            index++;
            return true;
        }
    }

    static int TowerHealth(string type) => type switch {
        "bolt" => 28,
        "cannon" => 34,
        "pulse" => 24,
        "frost" => 22,
        "spikes" => SpikeCharges,
        "mine" => 1,
        _ => 24
    };

    ImmutableArray<BuildingView> buildingsNow() => towers
        .Select(t => new BuildingView(t.Type, t.Cell, t.Hp, t.Max)).ToImmutableArray();

    static (int Damage, int Range, int Every, bool HitsAll) Stats(string type) => type switch {
        "bolt" => (4, 9, 6, false),
        "pulse" => (1, 4, 3, true),
        "cannon" => (9, 8, 8, false),
        "frost" => (0, 4, 2, true),
        _ => (0, 0, 1, false)
    };

    static void TriggerTrap(Slime mob, Dictionary<int, Building> traps, ImmutableArray<Target>.Builder shots) {
        if (!traps.TryGetValue(mob.Cell, out var trap) || trap.Hp <= 0) return;
        if (trap.Type == "mine") {
            mob.Health = 0;
            trap.Hp = 0;
        } else {
            if (mob.Kind == "apc") return;
            mob.Damage(SpikeDamage);
            trap.Hp--;
        }
        shots.Add(new(trap.Cell, mob.X, mob.Y));
    }

    ImmutableArray<Fence> FenceSnapshot() => fences
        .Select(f => new Fence(f.Key, f.Value, FenceHealth)).ToImmutableArray();

    (ImmutableArray<Frame> Frames, int Spawned, int Killed) Fight() {
        Dictionary<int, Queue<Slime>> pending = [];
        foreach (var order in orders) {
            if (!pending.TryGetValue(order.Cell, out var queue)) pending.Add(order.Cell, queue = new());
            var (kind, count) = order.Type switch {
                "giant" => ("giant", 1),
                "smart" => ("smart", 5),
                "runner" => ("runner", 6),
                "armored" => ("armored", 3),
                "ranged" => ("ranged", 2),
                "apc" => ("apc", 1),
                _ => ("swarm", 5)
            };
            for (int n = 0; n < count; n++) {
                var options = maze.RouteOptions[order.Cell];
                var path = kind switch {
                    "smart" => options.MinBy(option => option.Length)!,
                    _ => options[Random.Shared.Next(options.Length)]
                };
                int health = kind switch {
                    "giant" => 26 + 2 * last,
                    "armored" => 6 + last,
                    "runner" => 1,
                    "ranged" => 8 + last,
                    "apc" => 44 + 3 * last,
                    _ => 2 + last
                };
                queue.Enqueue(new(path, kind, health));
            }
        }

        List<Slime> mobs = [];
        var traps = towers.Where(t => IsTrap(t.Type)).ToDictionary(t => t.Cell);
        int spawned = 0, killed = 0;
        void AddMob(Slime mob) {
            mob.AssignId(++spawned);
            mobs.Add(mob);
        }

        var replay = ImmutableArray.CreateBuilder<Frame>();
        // Bound the replay allocation even if a new effect prevents a slime from moving.
        for (int tick = 0; tick < FightTickLimit && hp > 0 &&
             (pending.Values.Any(queue => queue.Count > 0) || mobs.Count > 0); tick++) {
            var shots = ImmutableArray.CreateBuilder<Target>();
            foreach (var mob in mobs)
                if (!mob.AimSiege(towers, fences, hp) && mob.Advance(tick, fences))
                    TriggerTrap(mob, traps, shots);
            foreach (var (gate, queue) in pending) {
                if (queue.Count == 0 || mobs.Any(mob => mob.Health > 0 && mob.Cell == gate)) continue;
                var next = queue.Dequeue();
                next.Spawn(tick);
                TriggerTrap(next, traps, shots);
                AddMob(next);
            }

            var aims = ImmutableArray.CreateBuilder<Target>();
            var siege = ImmutableArray.CreateBuilder<SiegeShot>();
            foreach (var tower in towers) {
                if (IsTrap(tower.Type)) continue;
                var (damage, range, every, hitsAll) = Stats(tower.Type);
                int x = tower.Cell % Maze.Width, y = tower.Cell / Maze.Width;
                var inRange = mobs.Where(m => m.Health > 0 && InReach(m)).ToList();
                if (inRange.Count == 0) continue;
                var lead = inRange.MinBy(m => m.Remaining)!;
                aims.Add(new(tower.Cell, lead.X, lead.Y));
                if (tick % every != 0) continue;
                if (tower.Type == "frost") {
                    foreach (var mob in inRange.Where(m => m.Slow()))
                        shots.Add(new(tower.Cell, mob.X, mob.Y));
                } else if (hitsAll) {
                    // The rune strikes the weakest few, not the whole crowd.
                    var weakest = inRange.OrderBy(m => m.Health).ThenBy(m => m.Remaining)
                        .Take(tower.Type == "pulse" ? PulseTargets : inRange.Count).ToList();
                    foreach (var mob in weakest) Strike(mob, damage);
                } else Strike(lead, damage);

                // A hit lands at once in the simulation, so the shot names the slime's own cell: the
                // board marks the target with a line, and no projectile has to chase a moving sprite
                void Strike(Slime mob, int amount) {
                    mob.Damage(amount);
                    shots.Add(new(tower.Cell, mob.X, mob.Y));
                }

                bool InReach(Slime mob) => tower.Type switch {
                    "frost" => Math.Abs(mob.X - x) <= 1 && Math.Abs(mob.Y - y) <= 1,
                    _ => Square(mob.X - x) + Square(mob.Y - y) <= range
                };
            }

            // Defense damage must resolve before a gunner can return fire.
            foreach (var mob in mobs) mob.FireSiege(tick, towers, fences, ref hp, siege);
            killed += mobs.RemoveAll(m => m.Health <= 0);
            // A destroyed carrier cannot release a passenger on this tick.
            for (int i = 0, count = mobs.Count; i < count; i++) {
                if (mobs[i].Release(tick, 3 + last) is not { } passenger) continue;
                TriggerTrap(passenger, traps, shots);
                AddMob(passenger);
            }
            killed += mobs.RemoveAll(m => m.Health <= 0);
            towers.RemoveAll(t => t.Hp <= 0);
            mobs.RemoveAll(m => m.Unloaded);

            foreach (var mob in mobs.Where(m => m.AtKeep))
                hp = Math.Max(0, hp - mob.ReachKeep());
            mobs.RemoveAll(m => m.AtKeep);
            replay.Add(new(hp, mobs.Select(m => m.Snapshot()).ToImmutableArray(),
                shots.ToImmutable(), aims.ToImmutable(), FenceSnapshot(), buildingsNow(), siege.ToImmutable()));
        }

        return (replay.ToImmutable(), spawned, killed);
    }

    static int Square(int x) => x * x;
}
