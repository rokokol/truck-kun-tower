namespace TruckKun.Tower.Training;

internal static class DefenseSearch {
    static readonly string[][] Profiles = [
        ["giant", "swarm", "runner"], ["ranged", "swarm", "runner"],
        ["apc", "runner", "swarm"], ["runner", "giant", "ranged", "swarm"], ["armored", "smart", "runner"]
    ];

    internal static Move Pick(Duel.PlanningView view, Func<Move, double> rank, Func<string, double> weight) {
        var scores = view.Purchases.ToDictionary(move => move, rank);
        var ranked = view.Purchases.OrderByDescending(move => scores[move]).ToArray();
        var shortlist = ranked.Take(3).ToList();
        var mines = ranked.Where(move => move.Action == "mine").ToList();
        if (mines.Count != 0) {
            if (!shortlist.Contains(mines[0])) shortlist.Add(mines[0]);
            while (shortlist.Count < 6 && mines.Any(move => !shortlist.Contains(move))) {
                shortlist.Add(mines.Where(move => !shortlist.Contains(move)).MaxBy(move =>
                    scores[move] + shortlist.Where(other => other.Action == "mine").Min(other =>
                        Distance(move.Cell!.Value, other.Cell!.Value)) / 7.0)!);
            }
        }
        var scenarios = Scenarios(view);
        if (scenarios.Length == 0) return ranked[0];
        var terrain = new Maze(view.Routes);
        var values = shortlist.Select(candidate => {
            var projected = scenarios.Select((orders, i) => Duel.ForecastDefense(view, terrain, candidate, orders, 104729 * (i + 1))).ToArray();
            double Loss(Duel.Forecast result) => SimulatedLoss(result, 0);
            return (Move: candidate, Score: scores[candidate] - weight("forecastMean") * projected.Average(Loss) -
                weight("forecastWorst") * projected.Max(Loss));
        });
        return values.MaxBy(candidate => candidate.Score).Move;
    }

    internal static Move[][] Scenarios(Duel.PlanningView view) {
        var gates = view.Routes.Select(options => options[0][0]).ToArray();
        var concentrated = from gate in gates
                           from profile in Profiles
                           select Build(profile, [gate]);
        var distributed = gates.Select((_, start) =>
            Build(["runner", "swarm", "smart"], [.. gates.Skip(start), .. gates.Take(start)]));
        return concentrated.Concat(distributed).Where(orders => orders.Length != 0)
            .DistinctBy(orders => string.Join(',', orders.Select(order => $"{order.Cell}:{order.Action}"))).ToArray();

        Move[] Build(string[] types, int[] targets) {
            int gold = view.AttackBudget;
            List<Move> plan = [];
            foreach (var type in types) Add(type);
            while (types.Any(Add)) { }
            return plan.ToArray();

            bool Add(string type) {
                int price = Duel.PriceOf(type)!.Value;
                if (price > gold || plan.Count(move => move.Action == type) >= Duel.PurchaseLimit(type)) return false;
                plan.Add(new(view.Wave, type, targets[plan.Count % targets.Length]));
                gold -= price;
                return true;
            }
        }
    }

    internal static double SimulatedLoss(Duel.Forecast result, double incomeWeight) =>
        result.HeartsLost + 0.025 * result.DefenseHealthLost - 0.15 * result.Killed + incomeWeight * result.NextAttackBonus;

    static int Distance(int a, int b) => Math.Abs(a % Maze.Width - b % Maze.Width) +
        Math.Abs(a / Maze.Width - b / Maze.Width);
}
