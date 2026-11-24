namespace TruckKun.Tower.Training;

internal sealed record Genome(double[] Weights, bool Tactical = true, bool Search = false, bool FullPlan = false, bool Hybrid = false, double[]? Network = null, double[]? Router = null, double[]? SecondExpert = null) {
    static readonly string[] Types = Duel.TrainablePurchases.ToArray();
    internal static readonly string[] Features = [.. Types, "coverage", "worstGate", "overlap", "keep",
        "trap", "fenceCover", "exposure", "distance", "concentration", "siege",
        "damagePerCoin", "crossfire", "trapTiming", "barrierTiming", "routeRisk", "siegeArrival", "forecastMean", "forecastWorst", "enemyIncome"];

    internal static Genome Baseline(bool tactical = false, bool search = false, bool fullPlan = false) => new([.. Types.Select(type => type switch {
        "bolt" or "pulse" => 0.7,
        "giant" or "armored" or "swarm" => 0.25,
        _ => 0.0
    }), 1.4, 0.9, 0.3, 0.4, 0.65, 0.65, 1.3, 0.6, 0.1, 0.8,
        1.2, 0.65, 0.65, 0.75, 1.1, 0.8, 0.7, 0.35, 0.3], tactical || search || fullPlan, search, fullPlan);

    internal static double[] NewRouter(Random random) => Enumerable.Range(0, 7).Select(_ => random.NextDouble() - 0.5).ToArray();

    internal Genome Mutate(Random random, double scale) {
        if (Network is not null) return this with {
            Network = Vary(Network), Router = Router is null ? null : Vary(Router),
            SecondExpert = SecondExpert is null ? null : Vary(SecondExpert)
        };
        double[] Vary(double[] source) {
            var next = (double[])source.Clone();
            int forcedIndex = random.Next(next.Length);
            for (int i = 0; i < next.Length; i++)
                if (i == forcedIndex || random.NextDouble() < 0.1)
                    next[i] = Math.Clamp(next[i] + (random.NextDouble() - 0.5) * scale, -4, 4);
            return next;
        }
        var weights = (double[])Weights.Clone();
        int forced = random.Next(weights.Length);
        for (int i = 0; i < weights.Length; i++)
            if (i == forced || random.NextDouble() < 0.25)
                weights[i] = Math.Clamp(weights[i] + (random.NextDouble() * 2 - 1) * scale, -3, 3);
        foreach (string key in new[] { "forecastMean", "forecastWorst", "enemyIncome" }) {
            int i = Array.IndexOf(Features, key);
            weights[i] = Math.Clamp(weights[i], 0.1, 3);
        }
        return this with { Weights = weights };
    }

    internal double Weight(string key) => Weights[Array.IndexOf(Features, key)];

    internal Move? Pick(Duel.PlanningView view, int side, int[]? expertUses = null) {
        if (view.Purchases.Length == 0) return null;
        if (Network is not null) {
            int e = Router is {} r && r[0] + r[1] * (view.Wave - 3) / 2 +
                r[2] * (view.Gold - 10) / 10 + r[3] * (view.Hearts - 5) / 5 +
                r[4] * (view.Buildings.Length - 3) / 3 + r[5] * (view.Fences.Length - 1) +
                r[6] * (view.Routes.Average(routes => routes[0].Length) - 20) / 10 > 0 ? 1 : 0;
            if (expertUses is not null) expertUses[e]++;
            return NeuralDefense.Pick(view, e == 0 ? Network : SecondExpert!, side);
        }
        var keep = view.Routes[0][0][^1];
        var defense = new double[Maze.Width * Maze.Height];
        var barriers = view.Fences.Select(fence => fence.Cell).ToHashSet();
        for (int cell = 0; cell < defense.Length; cell++) {
            defense[cell] = view.Buildings.Sum(building => building.Type switch {
                "mine" or "spikes" => building.Cell == cell ? 2 : 0,
                "cannon" => Duel.Covers(building.Type, building.Cell, cell) ? 1.4 : 0,
                _ => Duel.Covers(building.Type, building.Cell, cell) ? 1 : 0,
            }) + (barriers.Contains(cell) ? 1.5 : 0);
        }
        var occupied = view.Buildings.Select(tower => tower.Cell).ToHashSet();
        var tactics = Tactical ? new TacticalFeatures(view, side) : null;
        var averageThreat = view.Routes.ToDictionary(route => route[0][0], route => route.Average(path => path.Sum(cell => defense[cell])));
        return Search && side == Duel.Defender ? DefenseSearch.Pick(view, Rank, Weight) :
            view.Purchases.MaxBy(Rank);

        double Rank(Move move) {
            string type = move.Action!;
            int site = move.Cell!.Value;
            return Weight(type) + (side == Duel.Defender ? Placement(type, site) : Order(type, site)) +
                (tactics?.Score(type, site, Weight) ?? 0);
        }

        double Placement(string type, int site) {
            bool trap = type is "mine" or "spikes", fence = type == "fence";
            var gain = new double[view.Routes.Length];
            var overlap = new double[view.Routes.Length];
            var current = new double[view.Routes.Length];
            for (int gate = 0; gate < view.Routes.Length; gate++) {
                var routes = view.Routes[gate];
                foreach (var path in routes) {
                    for (int step = 0; step < path.Length; step++) {
                        int tile = path[step];
                        current[gate] += defense[tile] / routes.Length;
                        bool reached = trap || fence ? tile == site : Duel.Covers(type, site, tile);
                        if (!reached) continue;
                        gain[gate] += 1.0 / routes.Length;
                        int contact = fence && step > 0 ? path[step - 1] : tile;
                        overlap[gate] += Math.Min(defense[contact], 3) / routes.Length;
                    }
                }
            }
            int vulnerable = Array.IndexOf(current, current.Min());
            return Weight("coverage") * gain.Average() / 8 + Weight("worstGate") * gain[vulnerable] / 8 +
                Weight("overlap") * overlap.Average() / 14 + Weight("keep") / (1 + Distance(site, keep) / 4.0) +
                (trap ? Weight("trap") * gain.Sum() / 3 : 0) +
                (fence ? Weight("fenceCover") * overlap.Sum() / 5 : 0);
        }

        double Order(string type, int gate) {
            var routes = view.Routes.First(route => route[0][0] == gate);
            var shortest = routes.MinBy(path => path.Length)!;
            double exposure = type == "smart" ? shortest.Sum(cell => defense[cell]) : averageThreat[gate];
            double length = type == "smart" ? shortest.Length : routes.Average(path => path.Length);
            double siege = type is not ("ranged" or "armored") ? 0 : routes.Average(path => {
                int first = Array.FindIndex(path, cell => occupied.Any(tower => type == "ranged"
                    ? Duel.Covers(type, cell, tower) : Distance(cell, tower) == 1));
                return first < 0 ? 0 : 1.0 / (1 + first / 6.0);
            });
            return -Weight("exposure") * exposure / 18 - Weight("distance") * length / 24 +
                Weight("concentration") * view.OrderedGates.Count(cell => cell == gate) / 3 + Weight("siege") * siege;
        }
    }

    static int Distance(int a, int b) => Math.Abs(a % Maze.Width - b % Maze.Width) +
        Math.Abs(a / Maze.Width - b / Maze.Width);
}
