using System.Diagnostics;
using Xunit;

namespace TruckKun.Tower.Tests;

internal static class BotCostCollection {
    public const string Name = "Bot cost";
}

[CollectionDefinition(BotCostCollection.Name, DisableParallelization = true)]
public sealed class BotCostCollectionDefinition { }

[Collection(BotCostCollection.Name)]
public sealed class BotCostTests {
    // Each timed turn must fit 25 ms
    // The budget leaves a wide margin for a busy CI host
    // A slower inference pass turns this check red
    // We take the median of 15 turns
    // One scheduler pause cannot turn the check red
    static readonly TimeSpan TurnBudget = TimeSpan.FromMilliseconds(25);
    const int TurnSamples = 15;
    const int WarmupTurns = 10;
    const int MapSeed = 10;
    const int BattleSeed = 0;

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void Shipped_weights_load_and_fit_the_network(int side) {
        var weights = Weights(side);
        Assert.NotEmpty(weights);
        Assert.True(weights.Length == BotBrain.LegacyLength || weights.Length == BotBrain.Length,
            $"The shipped weights have {weights.Length} values and fit neither network size");
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void A_full_bot_turn_of_either_side_stays_within_the_budget(int side) {
        // Warm the JIT and the weights before any timing
        foreach (int wave in new[] { 1, 5 })
            for (int warm = 0; warm < WarmupTurns; warm++)
                PlayTurn(BoardAt(side, wave), side);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        foreach (int wave in new[] { 1, 5 }) {
            var samples = new List<double>(TurnSamples);
            for (int i = 0; i < TurnSamples; i++)
                samples.Add(MeasureTurn(BoardAt(side, wave), side));
            double median = Median(samples);
            Assert.True(median < TurnBudget.TotalMilliseconds,
                $"The median {Sides.Name(side)} turn at wave {wave} took {median:F3} ms, " +
                $"over the {TurnBudget.TotalMilliseconds:F0} ms budget");
        }
    }

    static double MeasureTurn(Duel duel, int side) {
        int resolved = duel.ResolvedWaves;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        int moves = PlayTurn(duel, side);
        long end = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(moves >= 1, "The bot made no move, so the turn did not run");
        Assert.False(duel.BotToMove, "The bot did not ready at the end of the turn");
        Assert.Equal(resolved, duel.ResolvedWaves);
        Assert.True(bytes >= 0, "The allocation counter went backwards");
        return Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    }

    static double Median(List<double> samples) {
        var sorted = samples.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    static double[] Weights(int side) => side == Duel.Defender ? BotModels.Defense : BotModels.Attack;

    // A turn ends at the ready call
    // The bot readies first, so no wave fight enters the timed turn
    static int PlayTurn(Duel duel, int side) {
        int resolved = duel.ResolvedWaves;
        int moves = PlanUntilReady(duel, side);
        Assert.False(duel.BotToMove, "The bot did not ready at the end of the turn");
        Assert.Equal(resolved, duel.ResolvedWaves);
        return moves;
    }

    // Plan one seat until its ready call succeeds
    static int PlanUntilReady(Duel duel, int side) {
        int wave = duel.ResolvedWaves + 1;
        int moves = 0;
        var weights = Weights(side);
        while (true) {
            var view = duel.ObservePlanning(side);
            Assert.Equal(wave, view.Wave);
            var move = BotBrain.Pick(view, weights, side);
            Assert.Null(duel.Apply(side, move ?? new Move(view.Wave, "ready", null)));
            moves++;
            if (move is null) break;
        }
        Assert.True(moves >= 1, "The turn made no move");
        return moves;
    }

    // Build a fresh board at the start of the requested wave
    // The earlier waves are set straight: the shipped attack wins most games early, so playing them
    // out would end the board before the later waves arrive
    static Duel BoardAt(int side, int wave) {
        var maze = new Maze(new Random(MapSeed));
        var duel = new Duel(maze, BattleSeed, wave - 1);
        duel.SeatBot(side);
        Assert.True(duel.TryTake(PlayerRef.Nobody, 1 - side, out _));
        Assert.True(duel.BotToMove, "The bot is not about to move");
        Assert.Equal(wave - 1, duel.ResolvedWaves);
        return duel;
    }
}
