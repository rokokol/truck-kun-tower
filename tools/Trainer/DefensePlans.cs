namespace TruckKun.Tower.Training;

internal static class DefensePlans {
    internal static Move[] Pick(Duel.PlanningView view, Genome genome) {
        var terrain = new Maze(view.Routes);
        var pool = Candidates(view, terrain, genome);
        var scenarios = DefenseSearch.Scenarios(view);
        if (pool.Length == 1) return pool[0];
        if (scenarios.Length == 0) return pool[1];
        return pool.MinBy(plan => {
            var results = scenarios.Select((attack, i) =>
                Duel.ForecastDefense(view, terrain, plan, attack, 104729 * (i + 1))).ToArray();
            int defeats = results.Count(result => result.HeartsLost == view.Hearts);
            var loss = results.Select(result => DefenseSearch.SimulatedLoss(result, genome.Weight("enemyIncome"))).ToArray();
            return (defeats, genome.Weight("forecastMean") * loss.Average() + genome.Weight("forecastWorst") * loss.Max());
        })!;
    }

    internal static Move[][] Candidates(Duel.PlanningView view, Maze terrain, Genome genome) {
        var greedy = genome with { Search = false, FullPlan = false };
        List<Move[]> plans = [[], Complete(view, null, greedy)];
        if (genome.Hybrid)
            plans.Add(Complete(view, null, greedy with { Search = true }));
        foreach (var group in view.Purchases.GroupBy(move => move.Action)) {
            var first = greedy.Pick(view with { Purchases = group.ToArray() }, Duel.Defender)!;
            plans.Add(Complete(view, first, greedy));
        }
        if (Duel.NextAttackBonusPerSurvivor(view.Wave) > 0) {
            var leastHealthy = view.Buildings.Select(b => (b.Cell, Health: (double)b.Hp / b.Max))
                .Concat(view.Fences.Select(f => (f.Cell, Health: (double)f.Hp / f.Max)))
                .OrderBy(item => item.Health).ThenBy(item => item.Cell).Take(2);
            foreach (var (cell, _) in leastHealthy) {
                var remove = new Move(view.Wave, "unbuild", cell);
                plans.Add([remove]);
                plans.Add(Complete(view, remove, greedy));
            }
        }
        return plans.DistinctBy(plan => string.Join(';', plan.Select(move => $"{move.Action}:{move.Cell}"))).ToArray();

        Move[] Complete(Duel.PlanningView state, Move? move, Genome policy) {
            List<Move> plan = [];
            while ((move ?? policy.Pick(state, Duel.Defender)) is { } next) {
                plan.Add(next);
                state = Duel.ProjectDefense(state, terrain, next);
                move = null;
            }
            return plan.ToArray();
        }
    }
}
