namespace TruckKun.Tower.Training;

internal sealed record TrainingOptions(int Seed = 73, int Population = 8, int Generations = 20, int Maps = 16, bool Search = false, bool FullPlans = false, bool Hybrid = false, bool Neural = false, bool BothNetworks = false, bool Mixture = false);
internal sealed record Score(int AttackWins, int HeartsTaken, int FirstWaveHeartsTaken,
    SortedDictionary<string, int>? AttackPurchases = null, SortedDictionary<string, int>? DefensePurchases = null, int[][]? ExpertUsesBySeat = null);
internal sealed record Generation(int Number, int[] MapSeeds, int[] BattleSeeds, int PopulationGames, int ArchiveGames,
    Score[][] PopulationScores, Genome Attack, Genome Defense);
internal sealed record Holdout(int Games, Score BaselinePair, Score EvolvedPair,
    Score EvolvedAttack, Score EvolvedDefense, Score TacticalPair, Score TacticalAttack, Score TacticalDefense, Score? SearchDefense);
internal sealed record TrainingReport(TrainingOptions Options, string[] Features, Generation[] Generations,
    int[] HoldoutMapSeeds, int[] HoldoutBattleSeeds, Holdout Holdout, AttackPanelResult[]? IndependentPanel, NeuralComparison[]? NeuralPanel = null, Score? NeuralAttackVsTactical = null);
internal sealed record NeuralComparison(string Style, Score Tactical, Score Neural);
internal sealed record AttackPanelResult(string Style, Score Tactical, Score SinglePurchase, Score FullPlan, Score Hybrid, Score Evolved);

internal static class GeneticTrainer {
    sealed record Scenario(int MapSeed, Maze Maze, int BattleSeed);
    sealed record Outcome(bool AttackWon, int HeartsTaken, int FirstWaveHeartsTaken);

    internal static TrainingReport Run(TrainingOptions options, Action<Generation>? progress = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Population, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Generations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Maps, 1);
        var random = new Random(options.Seed);
        var scenarioRandom = new Random(options.Seed ^ 0x62FE885B);
        var used = new HashSet<int>();
        var holdoutMaps = options.BothNetworks ? Scenarios(new Random(0xA9C31), 96) : Scenarios(scenarioRandom, options.Maps);
        var baseline = Genome.Baseline();
        var tactical = Genome.Baseline(tactical: true);
        var attack = options.BothNetworks ? Networks() : Start(tactical);
        var search = Genome.Baseline(search: true);
        var full = Genome.Baseline(fullPlan: true);
        var hybrid = full with { Hybrid = true };
        var defense = options.Neural || options.BothNetworks ? Networks() : Start(options.Hybrid ? hybrid : options.FullPlans ? full : options.Search ? search : tactical);
        var archive = new List<(Genome Attack, Genome Defense)>();
        var history = new List<Generation>();
        var winners = (Attack: baseline, Defense: baseline);
        for (int generation = 1; generation <= options.Generations; generation++) {
            var scenarios = Scenarios(scenarioRandom, options.Maps);
            var rivals = archive.Count == 0 ? [(baseline, baseline)] : new[] { (baseline, baseline), archive[^1] };
            if (options.BothNetworks) rivals = [.. rivals, (tactical, tactical)];
            var scores = attack.Select(a => defense.Select(d => Play(a, d, scenarios)).ToArray()).ToArray();
            var attackArchive = attack.Select(a => rivals.Select(pair => Play(a, pair.Item2, scenarios)).ToArray()).ToArray();
            var defenseArchive = defense.Select(d => rivals.Select(pair => Play(pair.Item1, d, scenarios)).ToArray()).ToArray();
            var attackParents = Parents(attack, scores.Select((row, i) => row.Concat(attackArchive[i]).ToArray()).ToArray(), true);
            var defenseParents = Parents(defense, defense.Select((_, i) => scores.Select(row => row[i])
                .Concat(defenseArchive[i]).ToArray()).ToArray(), false);
            winners = (attackParents.Best, defenseParents.Best);
            archive.Add(winners);
            archive.Add((attackParents.Specialist, defenseParents.Specialist));
            if (archive.Count > 2 && generation % 2 == 0) {
                var older = archive[random.Next(archive.Count - 2)];
                archive.Remove(older);
                archive.Add(older);
            }
            var result = new Generation(generation, scenarios.Select(s => s.MapSeed).ToArray(),
                scenarios.Select(s => s.BattleSeed).ToArray(), attack.Length * defense.Length * scenarios.Length,
                (attack.Length + defense.Length) * rivals.Length * scenarios.Length, scores, winners.Attack, winners.Defense);
            history.Add(result);
            progress?.Invoke(result);
            attack = Breed(attackParents);
            defense = Breed(defenseParents);
        }
        return new(options, Genome.Features, history.ToArray(), holdoutMaps.Select(s => s.MapSeed).ToArray(),
            holdoutMaps.Select(s => s.BattleSeed).ToArray(), new(holdoutMaps.Length, Evaluate(baseline, baseline), Evaluate(winners.Attack, winners.Defense),
                Evaluate(winners.Attack, baseline), Evaluate(baseline, winners.Defense),
                Evaluate(tactical, tactical), Evaluate(tactical, baseline), Evaluate(baseline, tactical),
                options.Search || options.FullPlans ? Evaluate(baseline, search) : null),
            options.FullPlans ? AttackPanel.Styles.Select(style => new AttackPanelResult(style,
                Against(tactical, style), Against(search, style), Against(full, style), Against(hybrid, style),
                Against(winners.Defense, style))).ToArray() : null,
            options.Neural || options.BothNetworks ? AttackPanel.Styles.Select(style => new NeuralComparison(style, Against(tactical, style), Against(winners.Defense, style))).ToArray() : null,
            options.BothNetworks ? Evaluate(winners.Attack, tactical) : null);

        Score Against(Genome d, string style) => Play(baseline, d, holdoutMaps, trackPurchases: true,
            attackOverride: view => AttackPanel.Pick(style, view));

        Score Evaluate(Genome a, Genome d) => Play(a, d, holdoutMaps, trackPurchases: true);

        Genome[] Networks() => Enumerable.Range(0, options.Population).Select(_ => baseline with { Network = NeuralDefense.Create(random), SecondExpert = options.Mixture ? NeuralDefense.Create(random) : null, Router = options.Mixture ? Genome.NewRouter(random) : null }).ToArray();

        Scenario[] Scenarios(Random source, int count) => Enumerable.Range(0, count).Select(_ => {
            int seed;
            do seed = source.Next(); while (!used.Add(seed));
            return new Scenario(seed, new Maze(new Random(seed)), source.Next());
        }).ToArray();

        Genome[] Start(Genome seed) => seed.Search || seed.FullPlan
            ? [baseline, tactical, seed, .. Enumerable.Range(3, options.Population - 3).Select(_ => seed.Mutate(random, 1.8))]
            : [baseline, tactical, .. Enumerable.Range(2, options.Population - 2).Select(_ => seed.Mutate(random, 1.8))];
        Genome[] Breed((Genome Best, Genome Specialist) parents) => [parents.Best, parents.Specialist,
            .. Enumerable.Range(2, options.Population - 2).Select(i =>
                (i % 2 == 0 ? parents.Best : parents.Specialist).Mutate(random, 0.9))];
    }

    static (Genome Best, Genome Specialist) Parents(Genome[] candidates, Score[][] scores, bool attack) {
        int Sign(int value) => attack ? value : -value;
        var ranked = Enumerable.Range(0, candidates.Length)
            .OrderByDescending(i => Sign(scores[i].Sum(score => score.AttackWins)))
            .ThenByDescending(i => Sign(scores[i].Sum(score => score.HeartsTaken))).ToArray();
        int best = ranked[0];
        int weakest = Enumerable.Range(0, scores[best].Length)
            .OrderBy(j => Sign(scores[best][j].AttackWins)).ThenBy(j => Sign(scores[best][j].HeartsTaken)).First();
        int specialist = ranked.Skip(1).OrderByDescending(i => Sign(scores[i][weakest].AttackWins))
            .ThenByDescending(i => Sign(scores[i][weakest].HeartsTaken)).First();
        return (candidates[best], candidates[specialist]);
    }

    static Score Play(Genome attack, Genome defense, Scenario[] maps, bool trackPurchases = false,
        Func<Duel.PlanningView, Move?>? attackOverride = null) {
        int wins = 0, damage = 0, opening = 0;
        var purchases = trackPurchases ? new[] {
            new SortedDictionary<string, int>(StringComparer.Ordinal),
            new SortedDictionary<string, int>(StringComparer.Ordinal)
        } : null;
        int[][]? uses = trackPurchases && (attack.Router is not null || defense.Router is not null) ? [[0, 0], [0, 0]] : null;
        foreach (var map in maps) {
            var result = Match(attack, defense, map, purchases, attackOverride, uses);
            if (result.AttackWon) wins++;
            damage += result.HeartsTaken;
            opening += result.FirstWaveHeartsTaken;
        }
        return new(wins, damage, opening, purchases?[Duel.Attacker], purchases?[Duel.Defender], uses);
    }

    static Outcome Match(Genome attack, Genome defense, Scenario map, SortedDictionary<string, int>[]? purchases,
        Func<Duel.PlanningView, Move?>? attackOverride, int[][]? uses) {
        var game = new Duel(map.Maze, map.BattleSeed);
        if (!game.TryTake(new(null, null), Duel.Defender, out _) || !game.TryTake(new(null, null), Duel.Attacker, out _))
            throw new InvalidOperationException("Cannot seat both bots");
        while (!game.Done) {
            int wave = game.ResolvedWaves + 1;
            Plan(defense, Duel.Defender);
            Plan(attack, Duel.Attacker);
            Apply(Duel.Defender, new(wave, "ready", null));
            Apply(Duel.Attacker, new(wave, "ready", null));
        }
        var result = game.TakeResult() ?? throw new InvalidOperationException("The duel has no result");
        return new(result.HeartsLeft == 0, result.Log.Sum(wave => wave.Damage), result.Log[0].Damage);

        void Plan(Genome policy, int side) {
            if (side == Duel.Defender && policy.FullPlan) {
                var plan = DefensePlans.Pick(game.ObservePlanning(side), policy);
                if (plan.Length == 0 && purchases?[side] is { } decisions)
                    decisions["pass"] = decisions.GetValueOrDefault("pass") + 1;
                foreach (var move in plan) PlayMove(move);
                return;
            }
            while (Pick() is { } purchase) PlayMove(purchase);

            Move? Pick() => side == Duel.Attacker && attackOverride is not null
                ? attackOverride(game.ObservePlanning(side)) : policy.Pick(game.ObservePlanning(side), side, policy.Router is null ? null : uses?[side]);

            void PlayMove(Move move) {
                Apply(side, move);
                if (purchases?[side] is { } counts && move.Action is { } type)
                    counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }
        void Apply(int side, Move move) {
            if (game.Apply(side, move) is not null) throw new InvalidOperationException($"Bot move rejected: {move}");
        }
    }
}
