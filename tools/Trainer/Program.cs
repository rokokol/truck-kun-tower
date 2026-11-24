using System.Globalization;
using System.Text.Json;
using TruckKun.Tower.Training;

if (args is ["--help"] or ["-h"]) {
    Console.WriteLine("Usage: dotnet run --project tools/Trainer -- [seed] [population] [generations] [maps-per-generation] [--search|--plans|--hybrid|--neural|--selfplay|--moe]");
    Console.WriteLine("Defaults: 73 8 20 16. The trainer writes progress to stderr and the report to stdout");
    return 0;
}
bool search = args.LastOrDefault() == "--search";
bool hybrid = args.LastOrDefault() == "--hybrid";
bool plans = hybrid || args.LastOrDefault() == "--plans";
bool neural = args.LastOrDefault() == "--neural";
bool moe = args.LastOrDefault() == "--moe";
bool both = moe || args.LastOrDefault() == "--selfplay";
if (search || plans || neural || both) args = args[..^1];
if (args.Length > 4 || args.Any(arg => !int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) {
    Console.Error.WriteLine("Pass up to four integers. See --help for the argument order");
    return 2;
}
var defaults = new TrainingOptions();
var numbers = args.Select(arg => int.Parse(arg, CultureInfo.InvariantCulture)).ToArray();
int Value(int index, int fallback) => index < numbers.Length ? numbers[index] : fallback;
var options = new TrainingOptions(Value(0, defaults.Seed), Value(1, defaults.Population),
    Value(2, defaults.Generations), Value(3, defaults.Maps), search, plans, hybrid, neural, both, moe);
if (options.Population < 3 || options.Generations < 1 || options.Maps < 1) {
    Console.Error.WriteLine("Use at least three bots per side, one generation and one map");
    return 2;
}
var report = GeneticTrainer.Run(options, result => Console.Error.WriteLine(
    $"Generation {result.Number}/{options.Generations}: {result.PopulationGames} population games, {result.ArchiveGames} archive games"));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) {WriteIndented = true}));
return 0;
