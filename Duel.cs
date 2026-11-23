using System.Collections.Immutable;
using System.Security.Cryptography;

public sealed class Duel {
    static long roomSequence;
    internal bool RandomOpponent { get; init; }
    internal long CreationOrder { get; } = Interlocked.Increment(ref roomSequence);
    internal static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(45);
    static readonly TimeSpan PollWindow = TimeSpan.FromSeconds(12);
    public const int Defender = 0, Attacker = 1;
    const int WaveCount = 5, ThinkTime = 60;
    const int ReplayTickMs = 270, ReplayMaxMs = 52000, FightTickLimit = 2048;
    const int CarrierPassengers = 5, SpikeCharges = 5, SpikeDamage = 4;
    const int FenceHealth = 16, KeepHealth = 10;
    const int RangedAmmo = 10, RangedCooldown = 4;
    const int StructureShotDamage = 4, KeepShotDamage = 1;
    const int RuneTargets = 3;
    static readonly int[] AttackerBonusPerSurvivor = [0, 1, 1, 2, 0];
    static readonly int[] DefenderBudget = [11, 12, 14, 16, 18], AttackerBudget = [12, 13, 16, 20, 25];
    static readonly ImmutableDictionary<string, int[]> AttackRows = new Dictionary<string, int[]> {
        ["bolt"] = Zone(3), ["cannon"] = Zone(2), ["pulse"] = Zone(2), ["frost"] = Zone(1),
        ["ranged"] = Zone(2)
    }.ToImmutableDictionary();
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
    internal readonly record struct Mob(int X, int Y, string Kind, bool Frozen, int Id, int Hp, int Max, int Facing);
    internal readonly record struct Fence(int Cell, int Hp, int Max);
    internal readonly record struct BuildingView(string Type, int Cell, int Hp, int Max);
    internal readonly record struct DefenseView(int Hp, ImmutableArray<BuildingView> Buildings, ImmutableArray<Fence> Fences);
    internal readonly record struct Target(int Cell, int X, int Y);
    internal readonly record struct SiegeShot(int Cell, int X, int Y, int MobId, bool Melee = false);
    readonly record struct SiegeTarget(int Cell, Building? Tower = null);
    internal readonly record struct Frame(int Hp, ImmutableArray<Mob> Mobs, ImmutableArray<Target> Shots,
        ImmutableArray<Target> Aims, ImmutableArray<Fence> Fences, ImmutableArray<BuildingView> Buildings,
        ImmutableArray<SiegeShot> Siege);

    // A chair belongs to an account, or to a guest holding the random token from the join.
    // A sideshow of keys used to be the whole identity; now it is only the guest's half of it.
    public sealed class Seat {
        public Guid? UserId { get; init; }
        internal DateTime LastSeen { get; set; } = DateTime.UtcNow;
        internal DateTime? LastPoll { get; set; }
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        internal bool Matches(PlayerRef who) => UserId is { } id ? who.UserId == id
            : who.SeatToken is { } token && TokensMatch(Token, token);
    }
    readonly Seat?[] seats = new Seat?[2];
    readonly Dictionary<string, Seat> watchers = new();
    readonly bool[] acted = new bool[2];
    int? swapBy;
    Maze maze = new();
    readonly bool[] ready = new bool[2];
    readonly int[] gold = [DefenderBudget[0], AttackerBudget[0]];
    readonly HashSet<int> refundableCells = [];
    readonly List<Building> towers = [];
    readonly Dictionary<int, int> fences = [];
    readonly List<Purchase> orders = [];
    ImmutableArray<Frame> frames = [];
    ImmutableArray<BuildingView> revealedBuildings = [];
    ImmutableArray<Fence> revealedFences = [];
    DefenseView? replayStart;
    int hp = KeepHealth;
    int last;

    internal sealed record ReplayMap(int Width, int Height, int[][] Paths, int[][] AlternatePaths, int WaveCount);
    internal sealed record ReplayWave(int ReplayMs, DefenseView ReplayStart, ImmutableArray<Frame> Frames);
    internal sealed record WaveWrite(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled,
        int Groups, ReplayWave Replay);
    internal sealed record ResultWrite(Guid Id, Guid? DefenderId, Guid? AttackerId,
        DateTime StartedAt, DateTime FinishedAt, int WavesPlayed,
        int HeartsLeft, string Winner, bool Survivor, WaveWrite[] Log, ReplayMap Map);

    Guid matchId = Guid.NewGuid();
    DateTime startedAt = DateTime.UtcNow;
    DateTime? deadline;
    DateTime replayEndsAt;
    int replayMs;
    readonly List<WaveWrite> log = [];
    ResultWrite? result;

    internal ResultWrite? TakeResult() {
        var finished = result;
        result = null;
        return finished;
    }

    public bool Joined => seats[Defender] is not null && seats[Attacker] is not null;
    public bool IsFull => Joined;
    public int Occupied => (seats[Defender] is null ? 0 : 1) + (seats[Attacker] is null ? 0 : 1);
    internal bool Done => last >= WaveCount || hp == 0;
    public bool Expired => Joined && !Done && deadline is { } end && DateTime.UtcNow >= end;
    int SecondsLeft => !Done && deadline is { } end
        ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds)) : 0;
    public Guid? UserAt(int side) => seats[side]?.UserId;
    public Seat? SeatAt(int side) => seats[side];

    public int SeatOf(PlayerRef who) {
        for (var side = 0; side < 2; side++) {
            if (seats[side]?.Matches(who) == true) return side;
        }
        return -1;
    }

    bool Watching(Seat seat) => watchers.ContainsValue(seat);
    internal bool CanMatch => RandomOpponent && !Joined && watchers.Count > 0;
    bool BothPresent(DateTime now) => seats.All(seat => seat is not null &&
        (Watching(seat) || (seat.LastPoll is { } poll && now - poll < PollWindow)));

    internal void Poll(int side, DateTime now) {
        if (seats[side] is not { } seat) return;
        seat.LastSeen = now;
        if (!Watching(seat)) seat.LastPoll = now;
    }

    internal bool Watch(PlayerRef who, string connection) {
        int side = SeatOf(who);
        if (side < 0) return false;
        var seat = seats[side]!;
        seat.LastSeen = DateTime.UtcNow;
        watchers[connection] = seat;
        return true;
    }

    internal void Unwatch(string connection, DateTime now) {
        if (!watchers.Remove(connection, out var seat) || Watching(seat)) return;
        seat.LastSeen = now;
        seat.LastPoll = null;
    }

    bool FirstWaveMissingMove => last == 0 && acted.Contains(false);

    internal bool ShouldRetire(DateTime now) {
        bool Gone(Seat seat) => !Watching(seat) && now - seat.LastSeen >= DisconnectGrace;
        if (Done) return now >= replayEndsAt && seats.OfType<Seat>().All(Gone);
        if (seats.OfType<Seat>().Any(Gone)) return true;
        return Joined && deadline is { } end && now >= end && BothPresent(now) && FirstWaveMissingMove;
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

    internal int ResolvedWaves => last;

    public object View(int seat, string? opponent = null) => new {
        randomOpponent = RandomOpponent,
        joined = Joined, seat, side = Sides.Name(seat), last, done = Done, locked = ready[seat],
        gold = gold[seat], hp, maxHp = KeepHealth,
        matchId, deadline, secondsLeft = SecondsLeft, replayMs,
        replayLeftMs = replayMs == 0 ? 0 : (int)Math.Clamp((replayEndsAt - DateTime.UtcNow).TotalMilliseconds, 0, replayMs),
        swap = swapBy is null ? null : (swapBy == seat ? "mine" : "theirs"),
        opponent, opponentId = UserAt(1 - seat),
        width = Maze.Width, height = Maze.Height,
        paths = maze.Routes.Values.ToArray(),
        alternatePaths = AlternatePaths(),
        fenceCells = maze.FenceCells,
        trapCells = seat == Defender ? TrapCells() : [],
        fences = seat == Defender ? FenceSnapshot() : revealedFences,
        towers = seat == Defender ? buildingsNow() : revealedBuildings,
        orders = seat == Attacker ? orders.ToArray() : [],
        refundableCells = seat == Defender ? refundableCells.ToArray() : Array.Empty<int>(),
        prices = Prices, limits = Limits, attackRows = AttackRows, frames, replayStart
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
            acted[seat] = true;
            return null;
        }
        if (move.Action == "unbuild") {
            if (seat != Defender || move.Cell is not int site) return Results.BadRequest();
            if (towers.FirstOrDefault(t => t.Cell == site) is { } built) {
                if (refundableCells.Remove(site)) gold[Defender] += PriceOf(built.Type)!.Value;
                towers.Remove(built);
                acted[seat] = true;
                return null;
            }
            if (fences.Remove(site, out _)) {
                if (refundableCells.Remove(site)) gold[Defender] += Prices.Fence;
                acted[seat] = true;
                return null;
            }
            return Results.Conflict();
        }
        if (move.Action == "ready") {
            ready[seat] = true;
            acted[seat] = true;
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
        if (fence || tower) refundableCells.Add(cell);
        acted[seat] = true;
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
        gold[Defender] = DefenderBudget[0];
        gold[Attacker] = AttackerBudget[0];
        refundableCells.Clear();
        towers.Clear();
        fences.Clear();
        orders.Clear();
        Array.Clear(ready);
        Array.Clear(acted);
        frames = [];
        revealedBuildings = [];
        revealedFences = [];
        replayStart = null;
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
        if (!Expired || !BothPresent(DateTime.UtcNow) || FirstWaveMissingMove) return false;
        ready[Defender] = true;
        ready[Attacker] = true;
        Resolve();
        return true;
    }

    int[][] AlternatePaths() => maze.RouteOptions.Values.SelectMany(options => options.Skip(1)).ToArray();

    void Resolve() {
        last++;
        int before = hp, groups = orders.Count;
        var start = new DefenseView(hp, buildingsNow(), FenceSnapshot());
        replayStart = start;
        var (replay, spawned, killed) = Fight();
        frames = replay;
        revealedBuildings = buildingsNow();
        revealedFences = FenceSnapshot();
        var now = DateTime.UtcNow;
        replayMs = Math.Min(frames.Length * ReplayTickMs, ReplayMaxMs);
        replayEndsAt = now.AddMilliseconds(replayMs);
        log.Add(new WaveWrite(last, hp, before - hp, spawned, killed, groups, new(replayMs, start, frames)));
        swapBy = null;
        orders.Clear();
        refundableCells.Clear();
        Array.Clear(ready);
        Array.Clear(acted);
        if (Done) {
            result = new ResultWrite(matchId, UserAt(Defender), UserAt(Attacker), startedAt, now, last, hp,
                hp == 0 ? "Attacker" : "Defender", hp > 0, log.ToArray(),
                new(Maze.Width, Maze.Height, maze.Routes.Values.ToArray(), AlternatePaths(), last));
            log.Clear();
        } else {
            gold[Defender] = DefenderBudget[last];
            gold[Attacker] = AttackerBudget[last] + AttackerBonusPerSurvivor[last] * (towers.Count + fences.Count);
            deadline = replayEndsAt.AddSeconds(ThinkTime);
        }
    }

    sealed class Slime(int[] path, string kind, int health) {
        int index;
        int facing = 1;
        int nextStep;
        int stepRemainder = StepScale - 1;
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
        public Mob Snapshot() => new(X, Y, Kind, Frozen, id, Health, maxHealth, facing);
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
            return ContactDamage(Kind);
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

        void ScheduleStep(int tick) {
            int interval = StepDuration(Kind) + stepRemainder;
            nextStep = tick + interval / StepScale;
            stepRemainder = interval % StepScale;
        }
        int FenceHits => FenceDamage(Kind);

        public void Spawn(int tick) {
            ScheduleStep(tick);
            for (int i = index + 1; i < path.Length; i++) {
                int dx = path[i] % Maze.Width - X;
                if (dx == 0) continue;
                facing = Math.Sign(dx);
                break;
            }
        }
        public bool Slow() {
            // Without this guard, a frost tower can freeze the same slime again before it moves.
            if (lastFrozenAt == index) return false;
            lastFrozenAt = index;
            return frozen = true;
        }
        public bool Frozen => frozen;
        public void Damage(int amount) => Health -= Kind == "armored" ? Math.Max(1, amount / 2) : amount;

        bool InSiegeReach(int cell) => InZone(AttackRows["ranged"], cell % Maze.Width - X, cell / Maze.Width - Y);
        int SiegeDistance(int cell) => Math.Max(Math.Abs(cell % Maze.Width - X), Math.Abs(cell / Maze.Width - Y));

        public bool AimSiege(List<Building> towers, Dictionary<int, int> fences, int keepHp) {
            if (Health <= 0 || keepHp <= 0) return false;
            if (Kind == "armored") return MeleeTower(towers) is not null;
            if (shotsLeft == 0) return false;
            if (siegeTarget is { } held && InSiegeReach(held.Cell) &&
                (held.Tower is { } heldTower ? heldTower.Hp > 0 && towers.Contains(heldTower) :
                    held.Cell == KeepCell || fences.ContainsKey(held.Cell))) return true;

            siegeTarget = null;
            int bestHp = int.MaxValue, bestDistance = int.MaxValue;
            foreach (var tower in towers) Consider(new(tower.Cell, tower), tower.Hp);
            foreach (var (cell, fenceHp) in fences) Consider(new(cell), fenceHp);
            if (siegeTarget is null && InSiegeReach(KeepCell))
                siegeTarget = new(KeepCell);
            return siegeTarget is not null;

            void Consider(SiegeTarget candidate, int targetHp) {
                int distance = SiegeDistance(candidate.Cell);
                if (targetHp <= 0 || !InSiegeReach(candidate.Cell) || targetHp > bestHp ||
                    (targetHp == bestHp && distance >= bestDistance)) return;
                siegeTarget = candidate;
                bestHp = targetHp;
                bestDistance = distance;
            }
        }

        Building? MeleeTower(List<Building> towers) => towers.Find(t => t.Hp > 0 && !IsTrap(t.Type) &&
            Math.Abs(t.Cell % Maze.Width - X) + Math.Abs(t.Cell / Maze.Width - Y) == 1);

        public void FireSiege(int tick, List<Building> towers, Dictionary<int, int> fences, ref int keepHp,
            ImmutableArray<SiegeShot>.Builder siege) {
            if (Kind == "armored") {
                if (Health <= 0 || keepHp <= 0 || MeleeTower(towers) is not { } adjacent ||
                    !SpendStep(tick, adjacent.Hp > ArmoredStructureDamage)) return;
                adjacent.Hp -= ArmoredStructureDamage;
                siege.Add(new(adjacent.Cell, X, Y, id, Melee: true));
                return;
            }
            if (tick < nextShot || !AimSiege(towers, fences, keepHp) || siegeTarget is not { } target) return;
            nextShot = tick + RangedCooldown;
            shotsLeft--;
            if (target.Tower is { } tower) tower.Hp -= StructureShotDamage;
            else if (target.Cell == KeepCell) keepHp = Math.Max(0, keepHp - KeepShotDamage);
            else if (fences[target.Cell] <= StructureShotDamage) fences.Remove(target.Cell);
            else fences[target.Cell] -= StructureShotDamage;
            siege.Add(new(target.Cell, X, Y, id));
        }

        bool SpendStep(int tick, bool repeatMelee = false) {
            if (tick < nextStep) return false;
            if (repeatMelee) nextStep = tick + ArmoredMeleeInterval;
            else ScheduleStep(tick);
            // A frozen slime loses one whole step, which is what the ice on the board shows
            if (frozen) {
                frozen = false;
                return false;
            }
            return true;
        }

        public bool Advance(int tick, Dictionary<int, int> fences, ImmutableArray<SiegeShot>.Builder siege) {
            if (Remaining == 0 || Unloading || tick < nextStep) return false;
            int nextCell = path[index + 1];
            bool blockedByFence = fences.TryGetValue(nextCell, out int health);
            if (!SpendStep(tick, Kind == "armored" && blockedByFence && health > ArmoredStructureDamage) ||
                PassengerAt(index + 1)) return false;
            if (blockedByFence) {
                if (Kind == "apc") {
                    fences.Remove(nextCell);
                    stoppedByFence = true;
                } else {
                    int left = health - FenceHits;
                    if (Kind == "armored") siege.Add(new(nextCell, X, Y, id, Melee: true));
                    if (left > 0) fences[nextCell] = left;
                    else fences.Remove(nextCell);
                }
                return false;
            }
            int dx = nextCell % Maze.Width - X;
            if (dx != 0) facing = Math.Sign(dx);
            index++;
            return true;
        }
    }

    const int StepScale = 4;
    static int StepDuration(string kind) => kind switch {
        "swarm" or "smart" => 7,
        "runner" or "balaclava" => StepScale,
        "giant" or "ranged" => 3 * StepScale,
        "armored" => 5 * StepScale / 2,
        "apc" => 4 * StepScale,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    static int ContactDamage(string kind) => kind switch { "giant" => 3, "balaclava" => 2, "apc" => 0, _ => 1 };
    const int ArmoredStructureDamage = 2, ArmoredMeleeInterval = 2;
    static int FenceDamage(string kind) => kind switch { "giant" => 3, "armored" => ArmoredStructureDamage, _ => 1 };
    static int UnitHealth(string kind, int wave) => kind switch {
        "giant" => (26 + 2 * wave) * 95 / 100,
        "armored" => 6 + wave,
        "runner" => 1,
        "ranged" => 8 + wave,
        "apc" => 44 + 3 * wave,
        "balaclava" => 3 + wave,
        _ => 2 + wave
    };

    internal static object Guide() {
        string[] units = ["swarm", "smart", "runner", "armored", "giant", "ranged", "apc", "balaclava"];
        string[] structures = ["bolt", "cannon", "pulse", "frost", "spikes", "mine", "fence", "keep"];
        return new {
            waveCount = WaveCount,
            budgets = Enumerable.Range(0, WaveCount).Select(index => new {
                wave = index + 1,
                defender = DefenderBudget[index],
                attacker = AttackerBudget[index],
                bonusPerStructure = AttackerBonusPerSurvivor[index]
            }).ToArray(),
            units = units.Select(type => new {
                type, hpFirst = UnitHealth(type, 1), hpLast = UnitHealth(type, WaveCount),
                damage = type == "ranged" ? StructureShotDamage : ContactDamage(type),
                keepDamage = type == "ranged" ? KeepShotDamage : ContactDamage(type),
                fenceDamage = type == "apc" ? 0 : FenceDamage(type),
                radius = type == "armored" ? 1 : Radius(type),
                interval = type switch { "ranged" => RangedCooldown, "armored" => ArmoredMeleeInterval, _ => 0 },
                stepTicks = (decimal)StepDuration(type) / StepScale
            }).ToArray(),
            structures = structures.Select(type => {
                int hp = type == "keep" ? KeepHealth : type == "fence" ? FenceHealth : TowerHealth(type);
                int radius = Radius(type);
                var (damage, interval) = radius == 0 ? (type == "spikes" ? SpikeDamage : 0, 0) : Stats(type);
                return new { type, hpFirst = hp, hpLast = hp, damage, radius, interval,
                    maxTargets = type == "pulse" ? RuneTargets : (int?)null };
            }).ToArray()
        };

        static int Radius(string type) => AttackRows.TryGetValue(type, out var rows) ? rows.Length / 2 : 0;
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

    static (int Damage, int Every) Stats(string type) => type switch {
        "bolt" => (4, 4),
        "pulse" => (1, 3),
        "cannon" => (13, 6),
        "frost" => (0, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    // Sample the circle at each cell center. Half a cell gives a full row on each axis.
    static int[] Zone(int radius) => Enumerable.Range(-radius, 2 * radius + 1)
        .Select(y => radius <= 1 ? radius : (int)Math.Sqrt(Square(radius + .5) - Square(y)))
        .ToArray();

    static bool InZone(int[] rows, int dx, int dy) {
        int row = dy + rows.Length / 2;
        return (uint)row < rows.Length && Math.Abs(dx) <= rows[row];
    }

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
                    _ => Maze.PickRoute(options, Random.Shared.NextDouble)
                };
                int health = UnitHealth(kind, last);
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
            var siege = ImmutableArray.CreateBuilder<SiegeShot>();
            foreach (var mob in mobs)
                if (!mob.AimSiege(towers, fences, hp) && mob.Advance(tick, fences, siege))
                    TriggerTrap(mob, traps, shots);
            foreach (var (gate, queue) in pending) {
                if (queue.Count == 0 || mobs.Any(mob => mob.Health > 0 && mob.Cell == gate)) continue;
                var next = queue.Dequeue();
                next.Spawn(tick);
                TriggerTrap(next, traps, shots);
                AddMob(next);
            }

            var aims = ImmutableArray.CreateBuilder<Target>();
            foreach (var tower in towers) {
                if (IsTrap(tower.Type)) continue;
                var (damage, every) = Stats(tower.Type);
                var rows = AttackRows[tower.Type];
                int x = tower.Cell % Maze.Width, y = tower.Cell / Maze.Width;
                var inRange = mobs.Where(m => m.Health > 0 && InZone(rows, m.X - x, m.Y - y)).ToList();
                if (inRange.Count == 0) continue;
                var runeTargets = tower.Type == "pulse"
                    ? inRange.OrderBy(m => Square(m.X - x) + Square(m.Y - y)).ThenBy(m => m.Remaining)
                        .Take(RuneTargets).ToArray()
                    : null;
                var lead = runeTargets?[0] ?? inRange.MinBy(m => m.Remaining)!;
                aims.Add(new(tower.Cell, lead.X, lead.Y));
                if (tick % every != 0) continue;
                if (tower.Type == "frost") {
                    foreach (var mob in inRange.Where(m => m.Slow()))
                        shots.Add(new(tower.Cell, mob.X, mob.Y));
                } else if (runeTargets is not null) {
                    foreach (var mob in runeTargets) Strike(mob, damage);
                } else Strike(lead, damage);

                // A hit lands at once in the simulation, so the shot names the slime's own cell: the
                // board marks the target with a line, and no projectile has to chase a moving sprite
                void Strike(Slime mob, int amount) {
                    mob.Damage(amount);
                    shots.Add(new(tower.Cell, mob.X, mob.Y));
                }

            }

            // Defense damage must resolve before a gunner can return fire.
            foreach (var mob in mobs) mob.FireSiege(tick, towers, fences, ref hp, siege);
            killed += mobs.RemoveAll(m => m.Health <= 0);
            // A destroyed carrier cannot release a passenger on this tick.
            for (int i = 0, count = mobs.Count; i < count; i++) {
                if (mobs[i].Release(tick, UnitHealth("balaclava", last)) is not { } passenger) continue;
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

    static double Square(double x) => x * x;
}
