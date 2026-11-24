using System.Diagnostics;
using System.Runtime.InteropServices;
using TruckKun.Tower;

// Measure one complete bot turn on real boards
// A turn repeats Duel.ObservePlanning, BotBrain.Pick, and Duel.Apply
// The turn ends at the ready call
// We measure defense and attack, because each side sees a different board
// We use the shipped weights in Bots/bot-weights.json
// Each board uses a seeded maze and a seeded duel
// Prior waves use both turns and the wave fight
// The bot readies first on each timed wave
// That order keeps Resolve and Fight outside the timed turn

int iterations = args.Length > 0 && int.TryParse(args[0], out int parsed) && parsed > 0 ? parsed : 2000;
const int Warmup = 200;
int[] mapSeeds = [10, 24, 73];
int[] battleSeeds = [0, 25, 13];
int[] sides = [Duel.Defender, Duel.Attacker];
int[] waves = Enumerable.Range(1, 5).ToArray();

PrintEnvironment();
Console.WriteLine();
PrintWeights();
Console.WriteLine();
PrintMeasurementShape();
Console.WriteLine();
Console.WriteLine($"Samples per side/wave/map: {iterations}, warmup turns per side/wave/map: {Warmup}");
Console.WriteLine($"Boards: {string.Join(", ", mapSeeds.Zip(battleSeeds, (map, battle) => $"{map}/{battle}"))}");
Console.WriteLine($"Total timed turns per side: {iterations * mapSeeds.Length * waves.Length}");

var perSide = new Dictionary<int, List<Sample>> {
    [Duel.Defender] = [],
    [Duel.Attacker] = []
};
var perWave = new Dictionary<(int Side, int Wave), List<Sample>>();

// Warm the JIT, the weights, and the heap before any timing
foreach (int side in sides)
    foreach (int wave in waves)
        for (int seed = 0; seed < mapSeeds.Length; seed++)
            for (int warm = 0; warm < Warmup; warm++)
                PlayTurn(BoardAt(side, wave, mapSeeds[seed], battleSeeds[seed]), side);

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

foreach (int side in sides)
    foreach (int wave in waves) {
        var cell = (Side: side, Wave: wave);
        var samples = new List<Sample>(iterations * mapSeeds.Length);
        for (int seed = 0; seed < mapSeeds.Length; seed++) {
            for (int i = 0; i < iterations; i++) {
                var duel = BoardAt(side, wave, mapSeeds[seed], battleSeeds[seed]);
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                int moves = PlayTurn(duel, side);
                long end = Stopwatch.GetTimestamp();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                samples.Add(new(Stopwatch.GetElapsedTime(start, end).TotalMicroseconds, bytes, moves));
            }
        }
        perSide[side].AddRange(samples);
        perWave[cell] = samples;
        Console.WriteLine($"  {Sides.Name(side)} wave {wave}: {samples.Count} turns collected");
    }

var stats = perSide.ToDictionary(pair => pair.Key, pair => Describe(pair.Value));
Console.WriteLine();
PrintSideTable(stats);
Console.WriteLine();
PrintWaveTable(perWave);
Console.WriteLine();
PrintDerived(perSide, stats);

static void PrintEnvironment() {
    Console.WriteLine("Environment");
    Console.WriteLine($"  Runtime: {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"  CLR: {Environment.Version}");
    Console.WriteLine($"  SDK image: {Environment.GetEnvironmentVariable("DOTNET_SDK_VERSION") ?? "unknown"}");
    Console.WriteLine($"  OS: {RuntimeInformation.OSDescription}");
    Console.WriteLine($"  Architecture: {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"  Logical processors: {Environment.ProcessorCount}");
    Console.WriteLine($"  Server GC: {System.Runtime.GCSettings.IsServerGC}");
    Console.WriteLine($"  Stopwatch frequency: {Stopwatch.Frequency:N0} Hz");
    Console.WriteLine($"  Container: {Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") ?? "unset"}");
    Console.WriteLine("  The container is a proxy for the production image, not the production host");
    Console.WriteLine("  Production host CPU speed can differ");
}

static void PrintWeights() {
    var file = new FileInfo("Bots/bot-weights.json");
    if (!file.Exists) throw new FileNotFoundException("The weight file is missing", file.FullName);
    var assembly = typeof(BotModels).Assembly;
    string name = assembly.GetManifestResourceNames()
        .Single(resource => resource.EndsWith("bot-weights.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(name)!;
    int attack = BotModels.Attack.Length;
    int defense = BotModels.Defense.Length;
    Console.WriteLine("Weights");
    Console.WriteLine("  Source file: Bots/bot-weights.json");
    Console.WriteLine($"  Repository file: {file.Length} bytes");
    Console.WriteLine($"  Embedded resource: {stream.Length} bytes");
    Console.WriteLine($"  Attack values: {attack}");
    Console.WriteLine($"  Defense values: {defense}");
    Console.WriteLine($"  Total values: {attack + defense}");
    Console.WriteLine($"  Legacy network size: {BotBrain.LegacyLength} values");
    Console.WriteLine($"  Current network size: {BotBrain.Length} values");
}

static void PrintMeasurementShape() {
    Console.WriteLine("Measurement shape");
    Console.WriteLine("  The bot readies before the opponent on each timed wave");
    Console.WriteLine("  This keeps Resolve and Fight outside the timed turn");
    Console.WriteLine("  Prior waves use both turns and the wave fight");
    Console.WriteLine("  Wall-clock samples include GC pauses");
    Console.WriteLine("  The allocation counter wraps only the turn");
}

static void PrintSideTable(Dictionary<int, Stats> stats) {
    Console.WriteLine("Per side, one complete bot turn");
    Console.WriteLine($"  {"side",-9} {"n",7} {"p50 us",10} {"p95 us",10} {"p99 us",10} {"mean us",10} {"mean B",10} {"p50 B",10} {"p95 B",10} {"p99 B",10} {"moves",9}");
    foreach (int side in new[] { Duel.Defender, Duel.Attacker }) {
        var s = stats[side];
        Console.WriteLine($"  {Sides.Name(side),-9} {s.Count,7} {s.P50,10:F1} {s.P95,10:F1} {s.P99,10:F1} " +
            $"{s.Mean,10:F1} {s.MeanBytes,10} {s.P50Bytes,10} {s.P95Bytes,10} {s.P99Bytes,10} " +
            $"{s.MinMoves}..{s.MaxMoves,-5}");
    }
}

static void PrintWaveTable(Dictionary<(int Side, int Wave), List<Sample>> perWave) {
    Console.WriteLine("Per wave, p50 / p95 / p99 microseconds");
    Console.WriteLine($"  {"wave",-5} {"defender",26} {"attacker",26}");
    foreach (int wave in Enumerable.Range(1, 5)) {
        string Row(int side) {
            var us = perWave[(side, wave)].Select(s => s.Micros).Order().ToArray();
            return $"{Percentile(us, 50),8:F1} {Percentile(us, 95),8:F1} {Percentile(us, 99),8:F1}";
        }
        Console.WriteLine($"  {wave,-5} {Row(Duel.Defender),26} {Row(Duel.Attacker),26}");
    }
}

static void PrintDerived(Dictionary<int, List<Sample>> perSide, Dictionary<int, Stats> stats) {
    var defense = stats[Duel.Defender];
    var attack = stats[Duel.Attacker];
    var mixed = perSide.Values.SelectMany(list => list).Select(s => s.Micros).Order().ToArray();
    Console.WriteLine("Derived figures");
    Console.WriteLine($"  {"side mix",-13} {"p50 us",9} {"p99 us",9} {"mean us",9} {"core frac",12} {"p50 frac",12} {"core %",10} {"128 CPU s",12}");
    Row("defense only", defense.P50, defense.P99, defense.Mean);
    Row("attack only", attack.P50, attack.P99, attack.Mean);
    Row("mixed 64/64", Percentile(mixed, 50), Percentile(mixed, 99), mixed.Average());
    Console.WriteLine();
    Console.WriteLine("Projection assumptions");
    Console.WriteLine("  Each of 128 solo boards has one bot");
    Console.WriteLine("  Each bot takes one planning turn in the wave");
    Console.WriteLine("  Turns run sequentially on one core");
    Console.WriteLine("  The sum uses the mean turn time");
    Console.WriteLine("  Percentiles describe one turn");
    Console.WriteLine("  Core fraction divides mean microseconds by 1,000,000");
    Console.WriteLine("  CPU seconds equal 128 times mean microseconds divided by 1,000,000");
    Console.WriteLine("  We exclude scheduling and I/O");
    Console.WriteLine("  We exclude network, JSON, SignalR fan-out, replay frames, and wave fight");

    static void Row(string label, double p50, double p99, double mean) {
        double core = mean / 1_000_000.0;
        double p50Core = p50 / 1_000_000.0;
        Console.WriteLine($"  {label,-13} {p50,9:F1} {p99,9:F1} {mean,9:F1} {core,12:F8} " +
            $"{p50Core,12:F8} {core * 100,10:F4} {128 * mean / 1_000_000.0,12:F4}");
    }
}

static Stats Describe(List<Sample> list) {
    var micros = list.Select(s => s.Micros).Order().ToArray();
    var bytes = list.Select(s => s.Bytes).Order().ToArray();
    return new(micros.Length, Percentile(micros, 50), Percentile(micros, 95), Percentile(micros, 99),
        micros.Average(), (long)Math.Round(bytes.Average()), LongPercentile(bytes, 50), LongPercentile(bytes, 95),
        LongPercentile(bytes, 99), list.Min(s => s.Moves), list.Max(s => s.Moves));
}

static double Percentile(double[] sorted, double p) {
    if (sorted.Length == 0) return double.NaN;
    return sorted[Index(p, sorted.Length)];
}

static long LongPercentile(long[] sorted, double p) {
    if (sorted.Length == 0) return 0;
    return sorted[Index(p, sorted.Length)];
}

static int Index(double p, int length) => Math.Clamp((int)Math.Ceiling(p / 100.0 * length) - 1, 0, length - 1);

static double[] Weights(int side) => side == Duel.Defender ? BotModels.Defense : BotModels.Attack;

// Plan one seat until its ready call succeeds
static int PlanUntilReady(Duel duel, int side) {
    int wave = duel.ResolvedWaves + 1;
    int moves = 0;
    var weights = Weights(side);
    while (true) {
        var view = duel.ObservePlanning(side);
        if (view.Wave != wave) throw new InvalidOperationException("The board changed wave during planning");
        var move = BotBrain.Pick(view, weights, side);
        var refusal = duel.Apply(side, move ?? new Move(view.Wave, "ready", null));
        if (refusal is not null) throw new InvalidOperationException("The duel refused a planning move");
        moves++;
        if (move is null) break;
    }
    if (moves == 0) throw new InvalidOperationException("The turn made no move");
    return moves;
}

// Time one complete bot turn with no wave fight
static int PlayTurn(Duel duel, int side) {
    int resolved = duel.ResolvedWaves;
    int moves = PlanUntilReady(duel, side);
    if (duel.BotToMove || duel.ResolvedWaves != resolved)
        throw new InvalidOperationException("The timed turn crossed a wave boundary");
    return moves;
}

// Build a fresh board at the start of the requested wave, with the bot about to move. The earlier
// waves are not played out: the shipped attack wins most games by the second wave, so a board that
// plays them cannot reach the later waves at all. The state is set straight instead.
static Duel BoardAt(int side, int wave, int mapSeed, int battleSeed) {
    var maze = new Maze(new Random(mapSeed));
    var duel = new Duel(maze, battleSeed, wave - 1);
    duel.SeatBot(side);
    if (!duel.TryTake(PlayerRef.Nobody, 1 - side, out _))
        throw new InvalidOperationException("The opponent chair stayed empty");
    if (!duel.BotToMove || duel.ResolvedWaves != wave - 1)
        throw new InvalidOperationException(
            $"The board did not reach wave {wave} for {Sides.Name(side)}, map {mapSeed}, battle {battleSeed}; " +
            $"resolved {duel.ResolvedWaves}, botToMove {duel.BotToMove}, done {duel.Done}, expired {duel.Expired}");
    return duel;
}

readonly record struct Sample(double Micros, long Bytes, int Moves);

readonly record struct Stats(int Count, double P50, double P95, double P99, double Mean, long MeanBytes,
    long P50Bytes, long P95Bytes, long P99Bytes, int MinMoves, int MaxMoves);
