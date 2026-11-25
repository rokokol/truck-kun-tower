namespace TruckKun.Tower.Training;

internal sealed class EditorActivity {
    public int Waves { get; private set; }
    public int ChangedWaves { get; private set; }
    public int Removals { get; private set; }
    public int Replacements { get; private set; }
    public int LimitHits { get; private set; }

    internal void Add(DefenseEditor.Edit[] edits) {
        Waves++;
        if (edits.Length != 0) ChangedWaves++;
        if (edits.Length == DefenseEditor.EditLimit) LimitHits++;
        foreach (var edit in edits)
            if (edit.Buy is null) Removals++;
            else Replacements++;
    }
}

internal sealed record EditorPolicy(EditorMemory Memory, double[] Weights);
internal sealed record EditorGeneration(int Number, int Games, int DefenseWins, int HeartsLost);
internal sealed record EditorRun(string Name, EditorPolicy Policy, EditorGeneration[] Generations);
internal sealed record EditorCell(string Editor, string Attacker, Score Score);
internal sealed record EditorGrid(TrainingOptions Options, int[] MapSeeds, int[] BattleSeeds, int EditLimit,
    EditorRun[] Runs, EditorCell[] Cells);

internal static partial class GeneticTrainer {
    internal static EditorGrid RunEditorGrid(TrainingOptions options, int evaluationMaps, Action<string, EditorGeneration>? progress = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Population, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Generations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Maps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(evaluationMaps, 1);
        var used = new HashSet<int>();
        var held = Scenarios(new Random(0x9C617F), evaluationMaps, used);
        var random = new Random(options.Seed ^ 0x60B7CA);
        var training = Enumerable.Range(0, options.Generations).Select(_ => Scenarios(random, options.Maps, used)).ToArray();
        var draft = Genome.Baseline() with { Network = BotModels.Defense };
        var shippedAttack = Genome.Baseline() with { Network = BotModels.Attack };
        var tactical = Genome.Baseline(tactical: true);
        var modes = new (string Name, EditorMemory Memory)[] {
            ("current", EditorMemory.Current), ("rnn", EditorMemory.Recurrent), ("cells", EditorMemory.CellHistory),
            ("rnn+cells", EditorMemory.Recurrent | EditorMemory.CellHistory)
        };
        var runs = new EditorRun[modes.Length];
        Parallel.For(0, runs.Length, i => runs[i] = Train(modes[i].Name, modes[i].Memory));
        var cells = new List<EditorCell>();
        Evaluate("draft-only", null);
        foreach (var run in runs) Evaluate(run.Name, run.Policy);
        return new(options, held.Select(map => map.MapSeed).ToArray(), held.Select(map => map.BattleSeed).ToArray(),
            DefenseEditor.EditLimit, runs, cells.ToArray());

        EditorRun Train(string name, EditorMemory memory) {
            var common = new Random(options.Seed);
            var extra = new Random(options.Seed ^ 0x139527);
            var mutate = new Random(options.Seed ^ 0x71A306);
            var population = Enumerable.Range(0, options.Population)
                .Select(_ => new Genome([], Network: DefenseEditor.Create(common, extra, memory))).ToArray();
            var history = new EditorGeneration[options.Generations];
            Genome best = population[0];
            for (int n = 0; n < training.Length; n++) {
                var maps = training[n];
                Score[][] scores = population.Select(candidate => {
                    var policy = new EditorPolicy(memory, candidate.Network!);
                    return new[] { Play(shippedAttack, draft, maps, editor: policy), Play(tactical, draft, maps, editor: policy) };
                }).ToArray();
                var parents = Parents(population, scores, attack: false);
                best = parents.Best;
                int games = maps.Length * scores[0].Length;
                var leader = scores[Array.IndexOf(population, best)];
                var generation = new EditorGeneration(n + 1, games * population.Length,
                    games - leader.Sum(score => score.AttackWins), leader.Sum(score => score.HeartsTaken));
                history[n] = generation;
                progress?.Invoke(name, generation);
                population = [parents.Best, parents.Specialist,
                    .. Enumerable.Range(2, options.Population - 2).Select(i =>
                        (i % 2 == 0 ? parents.Best : parents.Specialist).Mutate(mutate, 0.9))];
            }
            return new(name, new(memory, best.Network!), history);
        }

        void Evaluate(string name, EditorPolicy? editor) {
            cells.Add(new(name, "shipped", Play(shippedAttack, draft, held, trackPurchases: true, editor: editor)));
            cells.Add(new(name, "tactical", Play(tactical, draft, held, trackPurchases: true, editor: editor)));
            foreach (string style in AttackPanel.Styles)
                cells.Add(new(name, style, Play(tactical, draft, held, trackPurchases: true,
                    attackOverride: view => AttackPanel.Pick(style, view), editor: editor)));
        }
    }
}
