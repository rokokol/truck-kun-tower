using TruckKun.Tower.Training;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class DefenseEditorTests {
    [Fact]
    public void Editor_can_stop_or_replace_a_new_building_with_its_refund() {
        var game = NewGame();
        var bolt = game.ObservePlanning(Duel.Defender).Purchases.First(m => m.Action == "bolt");
        Assert.Null(game.Apply(Duel.Defender, bolt));
        var fence = game.ObservePlanning(Duel.Defender).Purchases.First(m => m.Action == "fence");
        Assert.Null(game.Apply(Duel.Defender, fence));
        Assert.Null(DefenseEditor.Refine(game, _ => 0, 5).FirstOrDefault());
        var edits = DefenseEditor.Refine(game, action => action is { Remove.Cell: var cell, Buy.Action: "pulse" }
            && cell == bolt.Cell ? 1 : 0, 5);
        Assert.Single(edits);
        var after = game.ObservePlanning(Duel.Defender);
        Assert.Equal(1, after.Gold);
        Assert.Contains(after.Buildings, b => b.Cell == bolt.Cell && b.Type == "pulse");
        Assert.Empty(game.ObservePlanning(Duel.Attacker).Buildings);
    }

    [Fact]
    public void Editor_does_not_repeat_a_draft_and_respects_the_edit_limit() {
        var game = NewGame();
        var bolt = game.ObservePlanning(Duel.Defender).Purchases.First(m => m.Action == "bolt");
        Assert.Null(game.Apply(Duel.Defender, bolt));
        double Score(DefenseEditor.Edit? edit) => edit?.Buy?.Action is "bolt" or "pulse" ? 1 : 0;
        Assert.Single(DefenseEditor.Refine(game, Score, 5));
        var fence = game.ObservePlanning(Duel.Defender).Purchases.First(m => m.Action == "fence");
        Assert.Null(game.Apply(Duel.Defender, fence));
        Assert.Single(DefenseEditor.Refine(game, edit => edit is { Buy: null } ? 1 : 0, 1));
        var after = game.ObservePlanning(Duel.Defender);
        Assert.Equal(1, after.Buildings.Length + after.Fences.Length);
    }

    [Fact]
    public void Old_building_does_not_pay_for_its_replacement() {
        var game = NewGame();
        var bolt = game.ObservePlanning(Duel.Defender).Purchases.First(m => m.Action == "bolt");
        Assert.Null(game.Apply(Duel.Defender, bolt));
        Assert.Null(game.Apply(Duel.Defender, new(1, "ready", null)));
        Assert.Null(game.Apply(Duel.Attacker, new(1, "ready", null)));
        var before = game.ObservePlanning(Duel.Defender);
        Assert.Equal(16, before.NextAttackBase);
        Assert.Equal(1, before.NextAttackBonus);
        var changes = DefenseEditor.Refine(game, edit => edit?.Buy is null && edit is not null ? 1 : 0, 5);
        Assert.Single(changes);
        Assert.Equal(before.Gold, game.ObservePlanning(Duel.Defender).Gold);
    }

    [Fact]
    public void Recurrent_state_carries_an_earlier_wave_into_a_later_wave() {
        double[] initial = new double[4], weights = new double[20];
        weights[0] = 1;
        weights[1] = 0.5;
        var afterAttack = DefenseEditor.Advance(initial, [1], weights, 0);
        var afterQuiet = DefenseEditor.Advance(afterAttack, [0], weights, 0);
        Assert.InRange(afterQuiet[0], 0.3, 0.4);
        Assert.Equal(0, DefenseEditor.Advance(initial, [0], weights, 0)[0]);
    }

    [Fact]
    public void Grid_compares_the_four_memories_and_a_draft_on_shared_holdouts() {
        var grid = GeneticTrainer.RunEditorGrid(new(Seed: 73, Population: 3, Generations: 1, Maps: 1), 2);
        Assert.Equal(5, grid.EditLimit);
        Assert.Equal(4, grid.Runs.Select(run => run.Policy.Memory).Distinct().Count());
        Assert.Equal(2, grid.MapSeeds.Distinct().Count());
        Assert.All(grid.Cells.GroupBy(cell => cell.Attacker), cells => {
            Assert.Equal(5, cells.Count());
            Assert.All(cells, cell => Assert.InRange(cell.Score.AttackWins, 0, grid.MapSeeds.Length));
            var control = Assert.Single(cells, c => c.Editor == "draft-only");
            Assert.Equal(0, control.Score.Editor!.ChangedWaves);
        });
    }

    static Duel NewGame() {
        var game = new Duel(new Maze(new Random(73)), 43);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        return game;
    }
}
