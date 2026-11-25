namespace TruckKun.Tower;

// The move chooser a solo opponent runs on: one forward pass per legal purchase of the turn and no
// game simulation, so a turn costs well under a millisecond. The trainer links this same file
// instead of carrying a copy, because a second copy of the pass would drift away from the weights.
internal static class BotBrain {
    const int Cells = Maze.Width * Maze.Height, Radius = 2, Window = Radius * 2 + 1;
    const int Embedding = 4, Hidden = 8, Globals = 4;
    static readonly string[] Types = Duel.TrainablePurchases.ToArray();
    const int Tile = 0, Gate = 1, Keep = 2, Road = 3, Progress = 4, Direction = 5, Hp = 9, Structure = 10;
    static readonly int Channels = Structure + Types.Length;
    static readonly int Patch = Embedding * Channels;
    const int Inputs = (Window * Window + 1) * Embedding + Globals + 1;
    static readonly int Head = Patch + Hidden * Inputs;
    // Three scalars describe the shape of the ways in, so a bot can tell a short route from a long one
    const int RouteInputs = 3;
    const int ShapedInputs = Inputs + RouteInputs;
    internal static int ShapedStride => ShapedInputs;
    internal static int PatchSize => Patch;
    // A checkpoint written before the wave history existed ends here and keeps its old meaning
    internal static readonly int LegacyLength = Head + Types.Length * (Hidden + 1);
    const int PastAttack = 0, PastAdded = 1, PastRemoved = 2, PastLost = 3;
    static readonly int HistoryChannels = (PastLost + 1) * Types.Length;
    // Before the route scalars, the same history layout, kept so an older checkpoint still plays
    internal static readonly int UnshapedLength = LegacyLength + Embedding * HistoryChannels;
    internal static readonly int Length = Patch + Hidden * ShapedInputs + Types.Length * (Hidden + 1)
        + Embedding * HistoryChannels;
    static readonly (int Cell, int Offset)[][] Windows = Enumerable.Range(0, Cells).Select(site =>
        (from dy in Enumerable.Range(-Radius, Window) from dx in Enumerable.Range(-Radius, Window)
         let x = site % Maze.Width + dx let y = site / Maze.Width + dy
         where (uint)x < Maze.Width && (uint)y < Maze.Height
         select (y * Maze.Width + x, ((dy + Radius) * Window + dx + Radius) * Embedding)).ToArray()).ToArray();

    // The three scalars that tell a short way in from a long one: shortest, average, route count
    internal static double[] RouteVector(Duel.PlanningView view) {
        var lengths = view.Routes.Select(paths => paths.Min(path => path.Length)).ToArray();
        return [lengths.Min() / 30.0, lengths.Average() / 30.0, view.Routes.Max(paths => paths.Length) / 4.0];
    }

    internal static double[] Create(Random random) => Enumerable.Range(0, Length).Select(_ => random.NextDouble() - 0.5).ToArray();

    internal static Move? Pick(Duel.PlanningView view, double[] weights, int side) {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length != LegacyLength && weights.Length != UnshapedLength && weights.Length != Length)
            throw new ArgumentException("Wrong spatial network size", nameof(weights));
        int stride = weights.Length == Length ? ShapedInputs : Inputs;
        if (view.Purchases.Length == 0) return null;
        var field = new double[Cells * Embedding];
        for (int cell = 0; cell < Cells; cell++) Add(cell, Tile, 1);
        Add(view.Routes[0][0][^1], Keep, 1);
        foreach (var routes in view.Routes) {
            Add(routes[0][0], Gate, 1);
            double share = 1.0 / routes.Length;
            foreach (var path in routes)
                for (int i = 0; i < path.Length; i++) {
                    Add(path[i], Road, share);
                    Add(path[i], Progress, share * i / (path.Length - 1));
                    if (i + 1 < path.Length) Add(path[i], Direction + ((path[i + 1] - path[i]) switch {
                        1 => 0, -1 => 1, Maze.Width => 2, _ => 3
                    }), share);
                }
        }
        foreach (var b in view.Buildings) Place(b.Cell, b.Type, b.Hp, b.Max);
        foreach (var f in view.Fences) Place(f.Cell, "fence", f.Hp, f.Max);
        bool shaped = weights.Length == Length;
        bool historical = shaped || weights.Length == UnshapedLength;
        if (historical && !view.WaveHistory.IsDefaultOrEmpty) AddHistory();
        var inputs = new double[stride];
        for (int i = 0; i < field.Length; i++) {
            field[i] = Math.Tanh(field[i]);
            inputs[Window * Window * Embedding + i % Embedding] += field[i] / Cells;
        }
        if (shaped) RouteVector(view).CopyTo(inputs, stride - 8);
        inputs[^5] = 1;
        inputs[^4] = view.Gold / 20.0;
        inputs[^3] = view.Hearts / 10.0;
        inputs[^2] = view.Wave / 5.0;
        inputs[^1] = view.AttackBudget / 20.0;
        var hidden = new double[Hidden];
        Move? best = null;
        double top = double.NegativeInfinity;
        foreach (var group in view.Purchases.GroupBy(move => move.Cell!.Value)) {
            int site = group.Key;
            Array.Clear(inputs, 0, Window * Window * Embedding);
            if (side == Duel.Attacker) {
                var paths = view.Routes.First(routes => routes[0][0] == site);
                foreach (var path in paths) {
                    double scale = 1.0 / (paths.Length * path.Length);
                    foreach (int tile in path)
                        foreach (var (cell, offset) in Windows[tile])
                            for (int k = 0; k < Embedding; k++)
                                inputs[offset + k] += field[cell * Embedding + k] * scale;
                }
                inputs[^1] = view.OrderedGates.Count(gate => gate == site) / 5.0;
            } else {
                foreach (var (cell, offset) in Windows[site]) Array.Copy(field, cell * Embedding, inputs, offset, Embedding);
            }
            for (int h = 0; h < Hidden; h++) {
                double sum = 0;
                int start = Patch + h * stride;
                for (int j = 0; j < stride; j++) sum += inputs[j] * weights[start + j];
                hidden[h] = Math.Tanh(sum);
            }
            foreach (var move in group) {
                int offset = Patch + Hidden * stride + Array.IndexOf(Types, move.Action) * (Hidden + 1);
                double score = weights[offset + Hidden];
                for (int h = 0; h < Hidden; h++) score += hidden[h] * weights[offset + h];
                if (score > top || (score == top && (site < best!.Cell || (site == best.Cell && string.CompareOrdinal(move.Action, best.Action) < 0)))) {
                    top = score;
                    best = move;
                }
            }
        }
        return best;

        void Add(int cell, int channel, double value) {
            for (int k = 0; k < Embedding; k++) field[cell * Embedding + k] += value * weights[k * Channels + channel];
        }
        void AddHistory() {
            foreach (var wave in view.WaveHistory) {
                int age = view.Wave - wave.Wave;
                if (age <= 0) continue;
                double recency = 1.0 / age;
                if (side == Duel.Defender) {
                    foreach (var group in wave.Attack) {
                        var paths = view.Routes.FirstOrDefault(routes => routes[0][0] == group.Cell);
                        if (paths is null) continue;
                        double pressure = group.Count * recency / paths.Length;
                        foreach (var path in paths)
                            foreach (int cell in path) AddPast(cell, PastAttack, group.Type, pressure);
                    }
                } else {
                    foreach (var site in wave.Added) AddPast(site.Cell, PastAdded, site.Type, recency);
                    foreach (var site in wave.Removed) AddPast(site.Cell, PastRemoved, site.Type, recency);
                    foreach (var site in wave.Lost) AddPast(site.Cell, PastLost, site.Type, recency);
                }
            }
        }
        void AddPast(int cell, int kind, string type, double value) {
            int index = Array.IndexOf(Types, type);
            if (index < 0) return;
            int offset = (weights.Length == Length ? Patch + Hidden * ShapedInputs : LegacyLength) + kind * Types.Length + index;
            for (int k = 0; k < Embedding; k++) field[cell * Embedding + k] += value * weights[offset + k * HistoryChannels];
        }
        void Place(int cell, string type, int hp, int max) {
            Add(cell, Hp, (double)hp / max);
            Add(cell, Structure + Array.IndexOf(Types, type), 1);
        }
    }
}
