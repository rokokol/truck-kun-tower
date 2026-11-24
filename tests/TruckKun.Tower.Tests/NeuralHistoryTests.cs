using System.Text.Json;
using TruckKun.Tower.Training;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class NeuralHistoryTests {
    const int Left = 3 * Maze.Width, Right = 9 * Maze.Width;
    const int LeftTrap = Left + 3, RightTrap = Right + 3;

    [Fact]
    public void Defender_can_react_to_the_gate_and_type_of_previous_groups() {
        var view = Board(Duel.Defender);
        var left = view with { WaveHistory = [new(1, [new("runner", Left, 3)], [], [], [])] };
        var right = view with { WaveHistory = [new(1, [new("runner", Right, 3)], [], [], [])] };
        var giant = view with { WaveHistory = [new(1, [new("giant", Left, 3)], [], [], [])] };
        AssertChangesPick(left, right, Duel.Defender);
        AssertChangesPick(left, giant, Duel.Defender);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("lost")]
    public void Attacker_can_react_to_past_defense_even_when_the_site_is_empty(string change) {
        var view = Board(Duel.Attacker);
        Duel.WaveSummary Summary(int site) => change switch {
            "added" => new(1, [], [new("mine", site)], [], []),
            "removed" => new(1, [], [], [new("mine", site)], []),
            _ => new(1, [], [], [], [new("mine", site)])
        };
        AssertChangesPick(view with { WaveHistory = [Summary(LeftTrap)] },
            view with { WaveHistory = [Summary(RightTrap)] }, Duel.Attacker);
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void Default_empty_and_unfinished_history_leave_the_same_pick(int side) {
        var view = Board(side);
        var unfinished = new Duel.WaveSummary(view.Wave, [new("runner", Left, 3)], [new("mine", LeftTrap)], [], []);
        foreach (var policy in Policies()) {
            var pick = policy.Pick(view, side);
            Assert.Equal(pick, policy.Pick(view with { WaveHistory = [] }, side));
            Assert.Equal(pick, policy.Pick(view with { WaveHistory = [unfinished] }, side));
        }
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void Unversioned_checkpoint_ignores_new_history_without_retraining(int side) {
        const int previousWeightCount = 1094;
        var current = NeuralDefense.Create(new Random(19));
        var previous = Genome.Baseline() with { Network = current[..previousWeightCount] };
        var checkpoint = JsonSerializer.Deserialize<Genome>(JsonSerializer.Serialize(previous))!;
        Array.Clear(current, previousWeightCount, current.Length - previousWeightCount);
        var withoutHistoryWeights = previous with { Network = current };
        var view = Board(side);
        var history = view with { WaveHistory = [new(1, [new("runner", Left, 3)], [], [], [new("mine", RightTrap)])] };
        Assert.Equal(withoutHistoryWeights.Pick(view, side), checkpoint.Pick(history, side));
    }

    static void AssertChangesPick(Duel.PlanningView first, Duel.PlanningView second, int side) =>
        Assert.Contains(Policies(), policy => policy.Pick(first, side) != policy.Pick(second, side));

    static IEnumerable<Genome> Policies() => Enumerable.Range(0, 128).Select(seed =>
        Genome.Baseline() with { Network = NeuralDefense.Create(new Random(seed)) });

    static Duel.PlanningView Board(int side) {
        int keep = 6 * Maze.Width + 7;
        int[] Route(int gate) => [.. Enumerable.Range(gate, 8),
            .. Enumerable.Range(1, 3).Select(i => gate + 7 + i * Math.Sign(keep - gate) * Maze.Width)];
        Move[] purchases = side == Duel.Defender
            ? [new(2, "bolt", LeftTrap + Maze.Width), new(2, "bolt", RightTrap - Maze.Width)]
            : [new(2, "runner", Left), new(2, "runner", Right)];
        return new(2, 12, 10, [[Route(Left)], [Route(Right)]], [], [], [], purchases);
    }
}
