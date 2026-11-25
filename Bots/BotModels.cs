using System.Text.Json;

namespace TruckKun.Tower;

// The shipped weights, read once per process. The file beside them is generated from a finished
// trainer run and names the run it came from, so a bot in the game can be traced to its training.
internal static class BotModels {
    sealed record Pack(double[] Attack, double[] Defense, Source? Source);
    sealed record Source(string? Trainer, int AttackRun, int DefenseRun, int Generation, int Architecture);

    static readonly Lazy<Pack> pack = new(() => {
        var assembly = typeof(BotModels).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("bot-weights.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var loaded = JsonSerializer.Deserialize<Pack>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Bot weights are unreadable");
        foreach (var weights in new[] { loaded.Attack, loaded.Defense })
            if (weights.Length != BotBrain.LegacyLength && weights.Length != BotBrain.UnshapedLength &&
                weights.Length != BotBrain.Length)
                throw new InvalidOperationException("Bot weights do not fit the network");
        return loaded;
    });

    internal static double[] Attack => pack.Value.Attack;
    internal static double[] Defense => pack.Value.Defense;
    internal static string Provenance => pack.Value.Source is { } source
        ? $"{source.Trainer} runs {source.AttackRun}/{source.DefenseRun}, generation {source.Generation}, {source.Architecture} weights"
        : "an unnamed trainer run";
}
