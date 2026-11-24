using TruckKun.Tower.Training;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class GeneticTrainerTests {
    [Fact]
    public void Seeded_tournaments_repeat_and_score_on_separate_scenarios() {
        var options = new TrainingOptions(Seed: 73, Population: 3, Generations: 2, Maps: 2);
        var first = GeneticTrainer.Run(options);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(GeneticTrainer.Run(options)));
        Assert.Equal(first.Features.Length, first.Features.Distinct().Count());
        Assert.NotEmpty(first.Holdout.TacticalDefense.DefensePurchases!);
        Assert.NotEmpty(first.Holdout.TacticalAttack.AttackPurchases!);
        Assert.Equal(2, first.Generations.Length);
        Assert.Equal(18, first.Generations[0].PopulationGames);
        Assert.NotEqual(first.Generations[0].MapSeeds, first.Generations[1].MapSeeds);
        Assert.Empty(first.HoldoutMapSeeds.Intersect(first.Generations.SelectMany(generation => generation.MapSeeds)));
        Assert.Equal(first.HoldoutMapSeeds.Length, first.Holdout.Games);
        Assert.InRange(first.Holdout.EvolvedPair.AttackWins, 0, first.Holdout.Games);
        var full = GeneticTrainer.Run(options with { FullPlans = true });
        Assert.Equal(first.HoldoutMapSeeds, full.HoldoutMapSeeds);
        Assert.Equal(first.HoldoutBattleSeeds, full.HoldoutBattleSeeds);
        for (int i = 0; i < first.Generations.Length; i++) {
            Assert.Equal(first.Generations[i].MapSeeds, full.Generations[i].MapSeeds);
            Assert.Equal(first.Generations[i].BattleSeeds, full.Generations[i].BattleSeeds);
        }
        Assert.Equal(AttackPanel.Styles.Length, full.IndependentPanel!.Length);
    }

    [Fact]
    public void Tactical_attack_avoids_a_visible_mine_on_equal_routes() {
        const int hazardous = 15, safe = 1;
        var view = new Duel.PlanningView(2, 4, 10, [[[hazardous, 16, 17]], [[safe, 16, 17]]],
            [new("mine", hazardous, 1, 1)], [], [], [new(2, "swarm", hazardous), new(2, "swarm", safe)]);
        var equallySafe = view with { Buildings = [] };
        var bot = Genome.Baseline(tactical: true);
        Assert.Equal(hazardous, bot.Pick(equallySafe, Duel.Attacker)!.Cell);
        Assert.Equal(safe, bot.Pick(view, Duel.Attacker)!.Cell);
        Assert.True(new TacticalFeatures(view, Duel.Attacker).Score("swarm", safe, _ => 1) >
            new TacticalFeatures(view, Duel.Attacker).Score("swarm", hazardous, _ => 1));
    }

    [Fact]
    public void Hybrid_candidates_include_the_legal_plan_from_single_purchase_search() {
        var terrain = new Maze(new Random(73));
        var game = new Duel(terrain, 43);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var view = game.ObservePlanning(Duel.Defender);
        var genome = Genome.Baseline(fullPlan: true);
        var search = genome with { Search = true, FullPlan = false };
        var state = view;
        List<Move> expected = [];
        while (search.Pick(state, Duel.Defender) is { } move) {
            expected.Add(move);
            state = Duel.ProjectDefense(state, terrain, move);
        }
        Assert.NotEmpty(expected);
        var hybrid = DefensePlans.Candidates(view, terrain, genome with { Hybrid = true });
        Assert.Contains(hybrid, plan => plan.SequenceEqual(expected));
        foreach (var purchase in DefensePlans.Pick(view, genome with { Hybrid = true }))
            Assert.Null(game.Apply(Duel.Defender, purchase));
    }

    [Fact]
    public void Complete_plans_can_leave_gold_or_demolish_without_a_refund() {
        var game = new Duel(new Maze(new Random(73)), 43);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var bolt = game.ObservePlanning(Duel.Defender).Purchases.First(move => move.Action == "bolt");
        Assert.Null(game.Apply(Duel.Defender, bolt));
        Assert.Null(game.Apply(Duel.Defender, new(1, "ready", null)));
        Assert.Null(game.Apply(Duel.Attacker, new(1, "ready", null)));
        var view = game.ObservePlanning(Duel.Defender);
        var terrain = new Maze(view.Routes);
        var plan = DefensePlans.Candidates(view, terrain, Genome.Baseline(tactical: true));
        Assert.Contains(plan, moves => moves.Length == 0);
        Assert.Contains(plan, moves => moves.Length >= 2);
        var remove = Assert.Single(plan, moves => moves is [{ Action: "unbuild" }]);
        var after = Duel.ProjectDefense(view, terrain, remove[0]);
        Assert.Equal(view.Gold, after.Gold);
        Assert.Empty(after.Buildings);
        Assert.Equal(1, Duel.ForecastDefense(view, terrain, [], [], 73).NextAttackBonus);
        Assert.Equal(0, Duel.ForecastDefense(view, terrain, remove, [], 73).NextAttackBonus);
        Assert.Equal(view.AttackBudget, after.AttackBudget);
        Assert.Single(game.ObservePlanning(Duel.Defender).Buildings);
    }

    [Fact]
    public void Mine_forecast_changes_when_a_runner_screens_the_giant() {
        int[] road = [79, 80, 81, 82];
        var terrain = new Maze([[road]]);
        var mine = new Move(1, "mine", 80);
        var view = new Duel.PlanningView(1, 11, 10, [[road]], [], [], [], [mine], AttackBudget: 12);
        var giant = new Move(1, "giant", road[0]);
        var runner = new Move(1, "runner", road[0]);
        Assert.True(Duel.ForecastDefense(view, terrain, mine, [giant, runner], 73).HeartsLost <
            Duel.ForecastDefense(view, terrain, mine, [runner, giant], 73).HeartsLost);
        Assert.Empty(view.Buildings);
    }

    [Fact]
    public void Defense_forecast_does_not_read_the_hidden_attack_queue() {
        var game = new Duel(new Maze(new Random(73)), 43);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var before = game.ObservePlanning(Duel.Defender);
        var bot = Genome.Baseline(search: true);
        var expected = bot.Pick(before, Duel.Defender);
        Assert.NotNull(expected);
        var order = game.ObservePlanning(Duel.Attacker).Purchases.First(move => move.Action == "giant");
        Assert.Null(game.Apply(Duel.Attacker, order));
        var after = game.ObservePlanning(Duel.Defender);
        Assert.Empty(after.OrderedGates);
        Assert.Equal(before.AttackBudget, after.AttackBudget);
        Assert.Equal(expected, bot.Pick(after, Duel.Defender));
    }

    [Fact]
    public void Attacker_plan_cannot_see_the_current_defense_purchases() {
        var game = new Duel(new Maze(new Random(73)), 43);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var before = game.ObservePlanning(Duel.Attacker);
        var build = game.ObservePlanning(Duel.Defender).Purchases.First(move => move.Action == "bolt");
        Assert.Null(game.Apply(Duel.Defender, build));
        var after = game.ObservePlanning(Duel.Attacker);
        Assert.Empty(after.Buildings);
        Assert.Equal(before.Purchases, after.Purchases);
        Assert.Null(game.Apply(Duel.Defender, new(1, "ready", null)));
        Assert.Null(game.Apply(Duel.Attacker, new(1, "ready", null)));
        Assert.Contains(game.ObservePlanning(Duel.Attacker).Buildings, tower => tower.Cell == build.Cell);
    }
}
