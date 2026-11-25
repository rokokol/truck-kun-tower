namespace TruckKun.Tower.Training;

[Flags]
internal enum EditorMemory { Current = 0, Recurrent = 1, CellHistory = 2 }

internal static class DefenseEditor {
    internal const int EditLimit = 5;
    const int Hidden = 8, RecurrentWidth = 4, StaticInputs = 19, Gates = 3;
    static readonly string[] Types = Duel.TrainablePurchases.ToArray();
    static readonly int CommonInputs = StaticInputs + 3 * Types.Length;
    static readonly int RecurrentInputs = 1 + Gates * Types.Length;
    static readonly int RecurrentWeights = RecurrentWidth * (RecurrentInputs + RecurrentWidth);
    static readonly int CellInputs = (Duel.WaveCount - 1) * 3 * Types.Length;
    static readonly int CommonWeights = Hidden * CommonInputs + Hidden;

    internal sealed record Edit(Move Remove, Move? Buy);

    internal static double[] Create(Random common, Random extra, EditorMemory memory) {
        int tail = (memory.HasFlag(EditorMemory.Recurrent) ? RecurrentWeights + Hidden * RecurrentWidth : 0)
            + (memory.HasFlag(EditorMemory.CellHistory) ? CellInputs * Hidden : 0);
        return [.. Enumerable.Range(0, CommonWeights).Select(_ => common.NextDouble() - 0.5),
            .. Enumerable.Range(0, tail).Select(_ => extra.NextDouble() - 0.5)];
    }

    internal static Edit[] Refine(Duel game, Func<Edit?, double> score, int limit) =>
        Refine(game, (_, _) => score, limit);

    internal static Edit[] Refine(Duel game, double[] weights, EditorMemory memory) {
        var first = game.ObservePlanning(Duel.Defender, includePurchases: false);
        var features = new WaveFeatures(first, weights, memory);
        return Refine(game, features.Scorer, EditLimit);
    }

    static Edit[] Refine(Duel game, Func<Duel.PlanningView, int, Func<Edit?, double>> scorer, int limit) {
        List<Edit> edits = [];
        var view = game.ObservePlanning(Duel.Defender, includePurchases: false);
        var terrain = new Maze(view.Routes);
        HashSet<string> seen = [Fingerprint(view)];
        while (edits.Count < limit) {
            var score = scorer(view, edits.Count);
            double stop = score(null);
            var candidates = Sites(view).SelectMany(cell => {
                Move remove = new(view.Wave, "unbuild", cell);
                return Duel.DefenseReplacementChoices(view, terrain, remove).Select(buy => new Edit(remove, buy))
                    .Prepend(new(remove, null));
            }).Select(edit => (Edit: edit, Score: score(edit))).Where(candidate => candidate.Score > stop)
              .OrderByDescending(candidate => candidate.Score);
            bool changed = false;
            foreach (var (edit, _) in candidates) {
                var next = Duel.ProjectDefenseEdit(view, terrain, edit.Remove, edit.Buy);
                if (!seen.Add(Fingerprint(next))) continue;
                if (game.Apply(Duel.Defender, edit.Remove) is not null ||
                    edit.Buy is { } buy && game.Apply(Duel.Defender, buy) is not null)
                    throw new InvalidOperationException("A projected defense edit is not legal");
                view = next;
                edits.Add(edit);
                changed = true;
                break;
            }
            if (!changed) break;
        }
        return edits.ToArray();
    }

    static IEnumerable<int> Sites(Duel.PlanningView view) => view.Buildings.Select(b => b.Cell)
        .Concat(view.Fences.Select(f => f.Cell)).Distinct().Order();

    static string Fingerprint(Duel.PlanningView view) => $"{view.Gold}/{string.Join(',', view.Refundable.Order())}/" +
        string.Join(';', view.Buildings.Select(b => (b.Cell, b.Type, b.Hp))
            .Concat(view.Fences.Select(f => (f.Cell, Type: "fence", f.Hp)))
            .OrderBy(b => b.Cell).ThenBy(b => b.Type, StringComparer.Ordinal).Select(b => $"{b.Cell}:{b.Type}:{b.Hp}"));

    sealed class WaveFeatures {
        readonly double[,] attack = new double[Maze.Width * Maze.Height, Types.Length];
        readonly double[] routes = new double[Maze.Width * Maze.Height];
        readonly double[] progress = new double[Maze.Width * Maze.Height];
        readonly List<int>?[] events = new List<int>?[Maze.Width * Maze.Height];
        readonly double[] hiddenContext = new double[Hidden];
        readonly double[] weights;
        readonly int cellOffset;
        readonly bool cellHistory;

        internal WaveFeatures(Duel.PlanningView view, double[] weights, EditorMemory memory) {
            this.weights = weights;
            cellHistory = memory.HasFlag(EditorMemory.CellHistory);
            var gates = view.Routes.OrderBy(paths => paths[0][0]).ToArray();
            foreach (var paths in gates)
                foreach (var path in paths)
                    for (int i = 0; i < path.Length; i++) {
                        routes[path[i]] += 1.0 / paths.Length;
                        progress[path[i]] += (double)i / (path.Length - 1) / paths.Length;
                    }
            int cursor = CommonWeights;
            double[] state = new double[RecurrentWidth];
            bool recurrent = memory.HasFlag(EditorMemory.Recurrent);
            if (!view.WaveHistory.IsDefaultOrEmpty) foreach (var wave in view.WaveHistory) {
                int age = view.Wave - wave.Wave;
                if (age <= 0) continue;
                var entries = recurrent ? new double[RecurrentInputs] : [];
                if (recurrent) entries[0] = 1;
                foreach (var entry in wave.Attack) {
                    int gate = Array.FindIndex(gates, paths => paths[0][0] == entry.Cell);
                    int type = Array.IndexOf(Types, entry.Type);
                    if (gate < 0 || type < 0) continue;
                    if (recurrent) entries[1 + gate * Types.Length + type] += entry.Count / 3.0;
                    foreach (var path in gates[gate])
                        foreach (int cell in path) attack[cell, type] += (double)entry.Count / age / gates[gate].Length;
                }
                if (recurrent) state = Advance(state, entries, weights, cursor);
                if (cellHistory && age < Duel.WaveCount) {
                    Add(wave.Added, 0);
                    Add(wave.Removed, 1);
                    Add(wave.Lost, 2);
                }
                void Add(IEnumerable<Duel.Site> sites, int kind) {
                    foreach (var site in sites) {
                        int type = Array.IndexOf(Types, site.Type);
                        if (type >= 0) (events[site.Cell] ??= []).Add(((age - 1) * 3 + kind) * Types.Length + type);
                    }
                }
            }
            if (recurrent) {
                cursor += RecurrentWeights;
                for (int h = 0; h < Hidden; h++)
                    for (int i = 0; i < state.Length; i++) hiddenContext[h] += state[i] * weights[cursor++];
            }
            cellOffset = cursor;
        }

        internal Func<Edit?, double> Scorer(Duel.PlanningView view, int used) {
            var sites = view.Buildings.ToDictionary(b => (b.Cell, b.Type), b => (double)b.Hp / b.Max);
            foreach (var f in view.Fences) sites[(f.Cell, "fence")] = (double)f.Hp / f.Max;
            int earners = sites.Count;
            double[] input = new double[CommonInputs];
            return edit => {
                Array.Clear(input);
                input[0] = 1;
                input[1] = view.Hearts / 10.0;
                input[2] = view.Wave / (double)Duel.WaveCount;
                input[3] = view.Gold / 20.0;
                input[4] = view.AttackBudget / 30.0;
                input[5] = view.NextAttackBase / 30.0;
                input[6] = view.NextAttackBonus / 2.0;
                input[7] = (view.NextAttackBase + view.NextAttackBonus * earners) / 40.0;
                input[8] = used / (double)EditLimit;
                input[9] = edit is null ? 1 : 0;
                int cell = edit?.Remove.Cell ?? -1;
                if (edit is not null) {
                    var source = sites.First(entry => entry.Key.Cell == cell);
                    string? replacement = edit.Buy?.Action;
                    bool refundable = view.Refundable.Contains(cell);
                    input[10] = source.Value;
                    input[11] = refundable ? 1 : 0;
                    input[12] = routes[cell] / Gates;
                    input[13] = routes[cell] == 0 ? 0 : progress[cell] / routes[cell];
                    input[14] = sites.Keys.Count(site => site.Cell != cell && Distance(site.Cell, cell) <= 2) / 12.0;
                    input[15] = (view.Gold + (refundable ? Duel.PriceOf(source.Key.Type) ?? 0 : 0)
                        - (Duel.PriceOf(replacement) ?? 0)) / 20.0;
                    input[16] = replacement is null ? -view.NextAttackBonus / 2.0 : 0;
                    input[17] = replacement is null ? 1 : 0;
                    input[18] = sites.Count / 20.0;
                    input[StaticInputs + Array.IndexOf(Types, source.Key.Type)] = 1;
                    if (replacement is not null) input[StaticInputs + Types.Length + Array.IndexOf(Types, replacement)] = 1;
                    for (int type = 0; type < Types.Length; type++) input[StaticInputs + 2 * Types.Length + type] = NearbyAttack(cell, type);
                }
                double score = 0;
                for (int h = 0; h < Hidden; h++) {
                    double sum = hiddenContext[h];
                    for (int i = 0; i < input.Length; i++) sum += input[i] * weights[h * input.Length + i];
                    if (cell >= 0 && cellHistory && events[cell] is { } local)
                        foreach (int feature in local) sum += weights[cellOffset + h * CellInputs + feature];
                    score += Math.Tanh(sum) * weights[Hidden * CommonInputs + h];
                }
                return score;
            };
        }

        double NearbyAttack(int cell, int type) {
            double sum = attack[cell, type];
            if (cell % Maze.Width > 0) sum += attack[cell - 1, type];
            if (cell % Maze.Width + 1 < Maze.Width) sum += attack[cell + 1, type];
            if (cell >= Maze.Width) sum += attack[cell - Maze.Width, type];
            if (cell < Maze.Width * (Maze.Height - 1)) sum += attack[cell + Maze.Width, type];
            return sum / 15.0;
        }
    }

    internal static double[] Advance(double[] state, double[] input, double[] weights, int offset) {
        var next = new double[RecurrentWidth];
        for (int h = 0; h < next.Length; h++) {
            double sum = 0;
            foreach (double value in input) sum += value * weights[offset++];
            foreach (double value in state) sum += value * weights[offset++];
            next[h] = Math.Tanh(sum);
        }
        return next;
    }

    static int Distance(int a, int b) => Math.Abs(a % Maze.Width - b % Maze.Width) + Math.Abs(a / Maze.Width - b / Maze.Width);
}
