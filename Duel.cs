using System.Collections.Immutable;
using System.Security.Cryptography;

public sealed class Duel {
    static long roomSequence;
    internal bool RandomOpponent { get; init; }
    internal long CreationOrder { get; } = Interlocked.Increment(ref roomSequence);
    internal static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(45);
    static readonly TimeSpan PracticeGrace = TimeSpan.FromMinutes(10);
    static readonly TimeSpan PollWindow = TimeSpan.FromSeconds(12);
    public const int Defender = 0, Attacker = 1;
    internal const int WaveCount = 5;
    const int ThinkTime = 60;
    const int ReplayTickMs = 270, ReplayMaxMs = 52000, FightTickLimit = 2048;
    const int CarrierPassengers = 5, SpikeCharges = 5;
    internal const int SpikeDamage = 4;
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
        Swarm: 4, Giant: 7, Runner: 3, Armored: 8, Ranged: 14, Apc: 13, Fence: 4);
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
    readonly record struct Purchase(string Type, int Cell) {
        public int Count => Type switch {
            "giant" or "apc" => 1,
            "smart" or "swarm" => 5,
            "runner" => 6,
            "armored" => 3,
            "ranged" => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(Type))
        };
    }
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
    int? botSide;
    readonly Dictionary<string, Seat> watchers = new();
    readonly bool[] acted = new bool[2];
    int? swapBy;
    Maze maze;
    readonly int? trainingSeed;

    public Duel() => maze = new Maze();

    internal Duel(Maze scenario, int battleSeed, int wavesPlayed = 0) {
        maze = scenario;
        trainingSeed = battleSeed;
        // A forecast and a measurement both start from a wave that is already behind, never from wave one
        if (wavesPlayed <= 0) return;
        last = Math.Min(wavesPlayed, WaveCount - 1);
        gold[Defender] = PurseAt(Defender, last + 1);
        gold[Attacker] = PurseAt(Attacker, last + 1);
    }
    readonly bool[] ready = new bool[2];
    readonly int[] gold = [DefenderBudget[0], AttackerBudget[0]];
    readonly HashSet<int> refundableCells = [];
    readonly List<Building> towers = [];
    readonly Dictionary<int, int> fences = [];
    readonly List<Purchase> orders = [];
    ImmutableArray<Frame> frames = [];
    ImmutableArray<BuildingView> revealedBuildings = [];
    ImmutableArray<Fence> revealedFences = [];
    internal readonly record struct Site(string Type, int Cell);
    internal readonly record struct OrderSummary(string Type, int Cell, int Count);
    internal sealed record WaveSummary(int Wave, ImmutableArray<OrderSummary> Attack, ImmutableArray<Site> Added,
        ImmutableArray<Site> Removed, ImmutableArray<Site> Lost);
    ImmutableArray<WaveSummary> waveHistory = [];
    DefenseView? replayStart;
    int hp = KeepHealth;
    int last;

    internal sealed record ReplayMap(int Width, int Height, int[][] Paths, int[][] AlternatePaths, int WaveCount);
    internal sealed record ReplayWave(int ReplayMs, DefenseView ReplayStart, ImmutableArray<Frame> Frames);
    internal sealed record WaveWrite(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled,
        int Groups, ReplayWave Replay);
    internal sealed record ResultWrite(Guid Id, Guid? DefenderId, Guid? AttackerId,
        DateTime StartedAt, DateTime FinishedAt, int WavesPlayed,
        int HeartsLeft, string Winner, bool Survivor, WaveWrite[] Log, ReplayMap Map,
        bool Practice, TaskCompletionSource<bool>? Archive);

    internal Task<bool>? PracticeArchive { get; private set; }
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
    // A solo board seats the bot in one chair. It holds no account and no token, so no player can
    // ever take it, and it counts as present whenever a turn is asked for
    internal bool Solo => botSide is not null;
    internal int? BotSeat => botSide;
    internal Guid MatchId => matchId;
    internal bool BotToMove => botSide is int side && Joined && !Done && !ready[side] && !Expired;

    internal void SeatBot(int side) {
        if (side is not (Defender or Attacker) || seats[side] is not null)
            throw new InvalidOperationException("The bot needs a free chair");
        seats[side] = new Seat();
        botSide = side;
        if (Joined) {
            startedAt = DateTime.UtcNow;
            deadline = null;
        }
    }
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
    // The bot counts as present without a connection, so a solo wave still closes when the clock runs out
    bool BothPresent(DateTime now) => seats[Defender] is not null && seats[Attacker] is not null &&
        Enumerable.Range(0, 2).All(side => side == botSide || seats[side] is { } seat &&
            (Watching(seat) || seat.LastPoll is { } poll && now - poll < PollWindow));

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
        // The bot never closes the socket, so it is never the reason a board is retired
        bool Gone(Seat seat) => !Watching(seat) && (botSide is not int bot || seat != seats[bot]) &&
            now - seat.LastSeen >= (Solo && !Done ? PracticeGrace : DisconnectGrace);
        if (Done) return now >= replayEndsAt && (botSide is int bot
            ? seats[1 - bot] is not {} human || Gone(human)
            : seats.OfType<Seat>().All(Gone));
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
            deadline = Solo ? null : startedAt.AddSeconds(ThinkTime);
        }
        return true;
    }

    // Nothing built, nothing queued, first wave still ahead: sides may move
    bool Untouched => last == 0 && towers.Count == 0 && fences.Count == 0 && orders.Count == 0 &&
        !ready[Defender] && !ready[Attacker];

    public bool CanSwitch => botSide is int bot ? last == 0 && !acted[1 - bot] : Untouched && Occupied > 0;
    public int? SwapRequestedBy => swapBy;

    // Two players: asking to switch puts the question to the other chair. Alone: just move.
    public bool AskSwitch(int side) {
        if (!CanSwitch) return false;
        if (botSide is int bot) {
            if (side == bot) return false;
            (seats[Defender], seats[Attacker]) = (seats[Attacker], seats[Defender]);
            botSide = side;
            Reset(keepMaze: true);
            return true;
        }
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

    internal sealed record PlanningView(int Wave, int Gold, int Hearts, int[][][] Routes,
        ImmutableArray<BuildingView> Buildings, ImmutableArray<Fence> Fences, int[] OrderedGates, Move[] Purchases,
        int AttackBudget = 0, ImmutableArray<WaveSummary> WaveHistory = default,
        int NextAttackBase = 0, int NextAttackBonus = 0, ImmutableArray<int> Refundable = default);

    internal PlanningView ObservePlanning(int seat, bool includePurchases = true) {
        Move[] purchases = !includePurchases || !Joined || Done || ready[seat] ? [] : LegalPurchases(seat, last + 1);
        return new(last + 1, gold[seat], hp, maze.RouteOptions.Values.Select(routes =>
                routes.Select(path => (int[])path.Clone()).ToArray()).ToArray(),
            seat == Defender ? buildingsNow() : revealedBuildings,
            seat == Defender ? FenceSnapshot() : revealedFences,
            seat == Attacker ? orders.Select(order => order.Cell).ToArray() : [], purchases,
            Done ? 0 : AttackIncome(last, revealedBuildings.Length + revealedFences.Length), waveHistory,
            last + 1 < WaveCount ? AttackerBudget[last + 1] : 0, NextAttackBonusPerSurvivor(last + 1),
            seat == Defender ? refundableCells.ToImmutableArray() : []);
    }

    Move[] LegalPurchases(int seat, int wave) {
        var trapSites = seat == Defender ? TrapCells().ToHashSet() : null;
        var sites = seat == Defender ? Enumerable.Range(0, Maze.Width * Maze.Height) : maze.Routes.Keys;
        return (from action in PurchaseTypes
            where PriceOf(action) <= gold[seat]
            from cell in sites
            where CheckPurchase(seat, action, cell, trapSites) is null
            select new Move(wave, action, cell)).ToArray();
    }

    public object View(int seat, string? opponent = null) => new {
        randomOpponent = RandomOpponent,
        joined = Joined, seat, side = Sides.Name(seat), last, done = Done, locked = ready[seat],
        gold = gold[seat], hp, maxHp = KeepHealth,
        matchId, deadline, secondsLeft = SecondsLeft, replayMs,
        replayLeftMs = replayMs == 0 ? 0 : (int)Math.Clamp((replayEndsAt - DateTime.UtcNow).TotalMilliseconds, 0, replayMs),
        swap = swapBy is null ? null : (swapBy == seat ? "mine" : "theirs"),
        opponent, opponentId = UserAt(1 - seat), bot = Solo, botOpponent = Solo && seat != botSide,
        canSwap = CanSwitch && swapBy is null,
        practiceReplay = PracticeArchive is { IsCompletedSuccessfully: true } saved && saved.Result,
        width = Maze.Width, height = Maze.Height,
        paths = maze.Routes.Values.ToArray(),
        alternatePaths = AlternatePaths(),
        fenceCells = maze.FenceCells,
        trapCells = seat == Defender ? TrapCells() : [],
        fences = seat == Defender ? FenceSnapshot() : revealedFences,
        towers = seat == Defender ? buildingsNow() : revealedBuildings,
        orders = seat == Attacker ? orders.ToArray() : [],
        refundableCells = seat == Defender ? refundableCells.ToArray() : Array.Empty<int>(),
        prices = Prices, limits = Limits, attackRows = AttackRows, frames, replayStart, waveHistory
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
            if (!RemoveDefense(site)) return Results.Conflict();
            acted[seat] = true;
            return null;
        }
        if (move.Action == "ready") {
            ready[seat] = true;
            acted[seat] = true;
            if (ready[Defender] && ready[Attacker]) Resolve();
            return null;
        }

        if (move.Cell is not int cell) return Results.BadRequest();
        var refusal = CheckPurchase(seat, move.Action, cell);
        if (refusal is not null) return refusal;

        Buy(seat, move.Action!, cell);
        acted[seat] = true;
        return null;
    }

    void Buy(int seat, string action, int cell) {
        gold[seat] -= PriceOf(action)!.Value;
        if (action == "fence") fences.Add(cell, FenceHealth);
        else if (seat == Defender) towers.Add(new Building(action, cell, TowerHealth(action)));
        else orders.Add(new(action, cell));
        if (seat == Defender) refundableCells.Add(cell);
    }

    bool RemoveDefense(int site) {
        if (towers.FirstOrDefault(t => t.Cell == site) is { } built) {
            if (refundableCells.Remove(site)) gold[Defender] += PriceOf(built.Type)!.Value;
            towers.Remove(built);
        } else if (fences.Remove(site)) {
            if (refundableCells.Remove(site)) gold[Defender] += Prices.Fence;
        } else return false;
        return true;
    }

    internal readonly record struct Forecast(int HeartsLost, int DefenseHealthLost, int Killed, int NextAttackBonus);

    internal static Forecast ForecastDefense(PlanningView defense, Maze terrain, Move candidate, Move[] attack, int seed) =>
        ForecastDefense(defense, terrain, [candidate], attack, seed);

    internal static PlanningView ProjectDefense(PlanningView defense, Maze terrain, Move purchase) {
        var draft = DefenseSimulation(defense, terrain, seed: 0);
        draft.SimulateDefenseMove(purchase);
        return draft.DefenseProjection(defense, draft.LegalPurchases(Defender, defense.Wave));
    }

    internal static Move[] DefenseReplacementChoices(PlanningView defense, Maze terrain, Move remove) {
        var draft = DefenseSimulation(defense, terrain, seed: 0);
        draft.SimulateDefenseMove(remove);
        int cell = remove.Cell!.Value;
        var trapSites = draft.TrapCells().ToHashSet();
        return PurchaseTypes.Where(type => draft.CheckPurchase(Defender, type, cell, trapSites) is null)
            .Select(type => new Move(defense.Wave, type, cell)).ToArray();
    }

    internal static PlanningView ProjectDefenseEdit(PlanningView defense, Maze terrain, Move remove, Move? replacement) {
        var draft = DefenseSimulation(defense, terrain, seed: 0);
        draft.SimulateDefenseMove(remove);
        if (replacement is not null) draft.SimulateDefenseMove(replacement);
        return draft.DefenseProjection(defense, []);
    }

    PlanningView DefenseProjection(PlanningView original, Move[] purchases) => original with {
        Gold = gold[Defender], Buildings = buildingsNow(), Fences = FenceSnapshot(),
        Purchases = purchases, Refundable = refundableCells.ToImmutableArray()
    };

    static Duel DefenseSimulation(PlanningView defense, Maze terrain, int seed) {
        var game = new Duel(terrain, seed) { last = defense.Wave, hp = defense.Hearts };
        game.gold[Defender] = defense.Gold;
        game.gold[Attacker] = defense.AttackBudget;
        foreach (var tower in defense.Buildings) game.towers.Add(new(tower.Type, tower.Cell, tower.Hp));
        foreach (var fence in defense.Fences) game.fences.Add(fence.Cell, fence.Hp);
        if (!defense.Refundable.IsDefaultOrEmpty) game.refundableCells.UnionWith(defense.Refundable);
        return game;
    }

    void SimulatedBuy(int seat, Move move) {
        if (move.Wave != last || move.Cell is not int cell || CheckPurchase(seat, move.Action, cell) is not null)
            throw new ArgumentException("Invalid forecast purchase", nameof(move));
        Buy(seat, move.Action!, cell);
    }

    void SimulateDefenseMove(Move move) {
        if (move.Action != "unbuild") {
            SimulatedBuy(Defender, move);
            return;
        }
        if (move.Wave != last || move.Cell is not int site || !RemoveDefense(site))
            throw new ArgumentException("Invalid forecast demolition", nameof(move));
    }

    internal static Forecast ForecastDefense(PlanningView defense, Maze terrain, Move[] plan, Move[] attack, int seed) {
        var game = DefenseSimulation(defense, terrain, seed);
        int demolishedHealth = 0;
        foreach (var move in plan) {
            if (move.Action == "unbuild") demolishedHealth += DefenseHealth();
            game.SimulateDefenseMove(move);
            if (move.Action == "unbuild") demolishedHealth -= DefenseHealth();
        }
        foreach (var order in attack) game.SimulatedBuy(Attacker, order);
        int before = DefenseHealth();
        var (_, _, killed) = game.Fight();
        int bonus = game.Done ? 0 : NextAttackBonusPerSurvivor(game.last) * (game.towers.Count + game.fences.Count);
        return new(defense.Hearts - game.hp, before - DefenseHealth() + demolishedHealth, killed, bonus);

        int DefenseHealth() => game.towers.Where(t => !IsTrap(t.Type)).Sum(t => t.Hp) + game.fences.Values.Sum();
    }

    static readonly string[] PurchaseTypes = [.. Limits.Keys.Order(StringComparer.Ordinal), "spikes", "mine"];

    IResult? CheckPurchase(int seat, string? action, int cell, HashSet<int>? trapSites = null) {
        if (PriceOf(action) is not int price) return Results.BadRequest();
        bool fence = action == "fence";
        bool trap = IsTrap(action);
        bool tower = trap || action is "bolt" or "pulse" or "cannon" or "frost";
        if ((fence || tower) != (seat == Defender)) return Results.BadRequest();
        bool valid = fence || trap ? maze.CanFence(cell) : tower ? maze.CanBuild(cell) : maze.Routes.ContainsKey(cell);
        if (!valid) return Results.BadRequest();
        if (Limits.TryGetValue(action!, out int limit)) {
            int count = fence ? fences.Count : tower ? towers.Count(t => t.Type == action) :
                orders.Count(o => o.Type == action);
            if (count >= limit) return TypedResults.Conflict(PurchaseLimitCode);
        }
        if (price > gold[seat] || (trap && !(trapSites?.Contains(cell) ?? TrapCells().Contains(cell))) ||
            (fence && fences.ContainsKey(cell)) || ((fence || tower) && towers.Exists(t => t.Cell == cell)))
            return Results.Conflict();
        return null;
    }

    internal static IReadOnlyList<string> TrainablePurchases => PurchaseTypes;
    internal static int PurchaseLimit(string action) => Limits.GetValueOrDefault(action, int.MaxValue);
    internal static int NextAttackBonusPerSurvivor(int wave) => wave < WaveCount ? AttackerBonusPerSurvivor[wave] : 0;
    internal static bool Covers(string type, int from, int to) => AttackRows.TryGetValue(type, out var rows) &&
        InZone(rows, to % Maze.Width - from % Maze.Width, to / Maze.Width - from / Maze.Width);

    static bool IsTrap(string? type) => type is "spikes" or "mine";

    int[] TrapCells() {
        var traps = towers.Where(t => IsTrap(t.Type)).Select(t => t.Cell).ToHashSet();
        var blocked = maze.RouteOptions.Values.SelectMany(options => options)
            .Where(path => path.Any(traps.Contains)).SelectMany(path => path).ToHashSet();
        return maze.FenceCells.Where(c => !blocked.Contains(c) && !fences.ContainsKey(c)).ToArray();
    }

    void Reset(bool keepMaze = false) {
        if (!keepMaze) maze = new Maze();
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
        waveHistory = [];
        log.Clear();
        swapBy = null;
        hp = KeepHealth;
        last = 0;
        result = null;
        PracticeArchive = null;
        matchId = Guid.NewGuid();
        startedAt = DateTime.UtcNow;
        deadline = Solo ? null : startedAt.AddSeconds(ThinkTime);
        replayMs = 0;
    }

    // The gold a side starts a wave with, before any survivor bonus the fight itself decides
    internal static int PurseAt(int seat, int wave) =>
        (seat == Defender ? DefenderBudget : AttackerBudget)[Math.Clamp(wave - 1, 0, WaveCount - 1)];

    internal static int? PriceOf(string? action) => action switch {
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

    static Site[] Sites(ImmutableArray<BuildingView> buildings, ImmutableArray<Fence> barriers) =>
        [.. buildings.Select(b => new Site(b.Type, b.Cell)), .. barriers.Select(f => new Site("fence", f.Cell))];

    void Resolve() {
        last++;
        int before = hp, groups = orders.Count;
        var start = new DefenseView(hp, buildingsNow(), FenceSnapshot());
        replayStart = start;
        var prior = Sites(revealedBuildings, revealedFences);
        var deployed = Sites(start.Buildings, start.Fences);
        var added = deployed.Where(site => refundableCells.Contains(site.Cell)).OrderBy(site => site.Cell).ToImmutableArray();
        var retained = deployed.Except(added);
        var removed = prior.Except(retained).OrderBy(site => site.Cell).ToImmutableArray();
        var attacks = orders.GroupBy(order => order).OrderBy(g => g.Key.Cell).ThenBy(g => g.Key.Type, StringComparer.Ordinal)
            .Select(g => new OrderSummary(g.Key.Type, g.Key.Cell, g.Count())).ToImmutableArray();
        var (replay, spawned, killed) = Fight();
        frames = replay;
        revealedBuildings = buildingsNow();
        revealedFences = FenceSnapshot();
        var lost = deployed.Except(Sites(revealedBuildings, revealedFences)).OrderBy(site => site.Cell).ToImmutableArray();
        waveHistory = waveHistory.Add(new(last, attacks, added, removed, lost));
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
            var defender = UserAt(Defender);
            var attacker = UserAt(Attacker);
            var archive = Solo && (defender is not null || attacker is not null)
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            PracticeArchive = archive?.Task;
            result = new ResultWrite(matchId, defender, attacker, startedAt, now, last, hp,
                hp == 0 ? "Attacker" : "Defender", hp > 0, log.ToArray(),
                new(Maze.Width, Maze.Height, maze.Routes.Values.ToArray(), AlternatePaths(), last), Solo, archive);
            log.Clear();
        } else {
            gold[Defender] = DefenderBudget[last];
            gold[Attacker] = AttackIncome(last, towers.Count + fences.Count);
            deadline = Solo ? null : replayEndsAt.AddSeconds(ThinkTime);
        }
    }

    static int AttackIncome(int waveIndex, int survivors) =>
        AttackerBudget[waveIndex] + AttackerBonusPerSurvivor[waveIndex] * survivors;

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
        public void Damage(int amount) => Health -= EffectiveDamage(Kind, amount);

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
    internal static int EffectiveDamage(string kind, int amount) =>
        kind == "armored" ? Math.Max(1, amount / 2) : amount;

    internal static int FenceDamage(string kind) => kind switch { "giant" => 3, "armored" => ArmoredStructureDamage, _ => 1 };
    internal static double MoveTicks(string kind) => (double)StepDuration(kind) / StepScale;

    internal static int UnitHealth(string kind, int wave) => kind switch {
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
                type, price = PriceOf(type),
                count = type == "balaclava" ? CarrierPassengers : new Purchase(type, 0).Count,
                hpFirst = UnitHealth(type, 1), hpLast = UnitHealth(type, WaveCount),
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
                return new { type, price = PriceOf(type), hpFirst = hp, hpLast = hp, damage, radius, interval,
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

    internal static (int Damage, int Every) Stats(string type) => type switch {
        "bolt" => (4, 6),
        "pulse" => (1, 3),
        "cannon" => (13, 7),
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
        Dictionary<int, int> gateSlots = [];
        foreach (var order in orders) {
            if (!pending.TryGetValue(order.Cell, out var queue)) pending.Add(order.Cell, queue = new());
            var kind = order.Type;
            for (int n = 0; n < order.Count; n++) {
                var options = maze.RouteOptions[order.Cell];
                int slot = gateSlots.GetValueOrDefault(order.Cell);
                gateSlots[order.Cell] = slot + 1;
                var path = kind == "smart" ? options.MinBy(option => option.Length)! :
                    Maze.PickRoute(options, trainingSeed is int seed
                        ? new Random(RouteSeed(seed, last, order.Cell, slot)).NextDouble
                        : Random.Shared.NextDouble);
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
            if (trainingSeed is null)
                replay.Add(new(hp, mobs.Select(m => m.Snapshot()).ToImmutableArray(),
                    shots.ToImmutable(), aims.ToImmutable(), FenceSnapshot(), buildingsNow(), siege.ToImmutable()));
        }

        return (replay.ToImmutable(), spawned, killed);
    }

    static int RouteSeed(int seed, int wave, int gate, int slot) {
        uint key = unchecked((uint)seed + (uint)((wave * Maze.Width * Maze.Height + gate) * 128 + slot));
        key ^= key >> 16;
        key = unchecked(key * 0x7feb352d);
        key ^= key >> 15;
        key = unchecked(key * 0x846ca68b);
        return (int)((key ^ (key >> 16)) & int.MaxValue);
    }

    static double Square(double x) => x * x;
}
