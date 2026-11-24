using TruckKun.Tower.Training;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class NeuralDefenseTests {
    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void Router_dispatches_to_the_selected_expert(int side) {
        var game = new Duel(new Maze(new Random(19)), 10);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var view = game.ObservePlanning(side);
        var policy = Genome.Baseline() with { Network = NeuralDefense.Create(new Random(6)) };
        var other = Enumerable.Range(0, 100).Select(i => NeuralDefense.Create(new Random(i)))
            .First(weights => NeuralDefense.Pick(view, weights, side) != policy.Pick(view, side));
        foreach (int expert in new[] { 0, 1 }) {
            var gate = Genome.NewRouter(new Random(1));
            Array.Clear(gate);
            gate[0] = expert == 0 ? -1 : 1;
            var mixture = policy with { Router = gate, SecondExpert = other };
            int[] calls = [0, 0];
            Assert.Equal(NeuralDefense.Pick(view, expert == 0 ? policy.Network! : other, side), mixture.Pick(view, side, calls));
            Assert.Equal(1, calls[expert]);
            Assert.Equal(1, calls.Sum());
        }
    }

    [Fact]
    public void Neural_attack_uses_its_weights_and_only_buys_at_legal_gates() {
        var game = new Duel(new Maze(new Random(19)), 10);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var view = game.ObservePlanning(Duel.Attacker);
        var moves = Enumerable.Range(0, 16).Select(seed => {
            var policy = Genome.Baseline() with { Network = NeuralDefense.Create(new Random(seed)) };
            var move = policy.Pick(view, Duel.Attacker)!;
            Assert.Contains(move, view.Purchases);
            return move;
        });
        Assert.True(moves.Distinct().Count() > 1);
    }

    [Fact]
    public void A_directional_detour_can_change_which_site_the_network_prefers() {
        int w = Maze.Width, row = 5 * w;
        Move[] purchases = [new(1, "bolt", row + 4), new(1, "bolt", row + 5)];
        Duel.PlanningView Board(int dy) {
            int[] path = [row, row+1, row+2, row+3, row+3+dy, row+4+dy, row+5+dy, row+6+dy, row+6, row+7];
            return new(1, 11, 10, [[path]], [], [], [], purchases);
        }
        var north = Board(-w);
        var south = Board(w);
        Assert.Contains(Enumerable.Range(0, 256), seed => {
            var weights = NeuralDefense.Create(new Random(seed));
            return NeuralDefense.Pick(north, weights) != NeuralDefense.Pick(south, weights);
        });
    }

    [Fact]
    public void Different_networks_choose_different_legal_moves_without_changing_the_parent() {
        var game = new Duel(new Maze(new Random(19)), 10);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var view = game.ObservePlanning(Duel.Defender);
        var parent = NeuralDefense.Create(new Random(18));
        var policy = Genome.Baseline() with { Network = parent };
        var original = parent.ToArray();
        HashSet<Move> picks = [];
        for (int i = 0; i < 24; i++) {
            var child = policy.Mutate(new Random(i), 1.8);
            var move = child.Pick(view, Duel.Defender)!;
            Assert.Contains(move, view.Purchases);
            Assert.Equal(move, child.Pick(view with { Purchases = view.Purchases.Reverse().ToArray() }, Duel.Defender));
            picks.Add(move);
        }
        Assert.True(picks.Count > 1);
        Assert.Equal(original, parent);
        Assert.Null(policy.Pick(view with { Purchases = [] }, Duel.Defender));
    }
}
