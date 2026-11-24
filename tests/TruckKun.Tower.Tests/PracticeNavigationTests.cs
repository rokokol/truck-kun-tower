using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class PracticeNavigationTests {
    [Fact]
    public void Solo_swap_clears_the_hidden_bot_plan_and_preserves_the_room_and_map() {
        var game = new Duel(new Maze(new Random(73)), 13);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out var chair));
        game.SeatBot(Duel.Attacker);
        var attacker = game.ObservePlanning(Duel.Attacker);
        var oldMatch = game.MatchId;
        Assert.Null(game.Apply(Duel.Attacker, attacker.Purchases.First()));
        Assert.Null(game.Apply(Duel.Attacker, new(1, "ready", null)));

        Assert.True(game.AskSwitch(Duel.Defender));

        Assert.Equal(Duel.Attacker, game.SeatOf(new(null, chair.Token)));
        Assert.Equal(Duel.Defender, game.BotSeat);
        Assert.True(game.BotToMove);
        Assert.NotEqual(oldMatch, game.MatchId);
        var swapped = game.ObservePlanning(Duel.Attacker);
        Assert.Equal(attacker.Gold, swapped.Gold);
        Assert.Empty(swapped.OrderedGates);
        Assert.Equal(string.Join(',', attacker.Routes.SelectMany(r => r[0])),
            string.Join(',', swapped.Routes.SelectMany(r => r[0])));
        Assert.Null(game.SwapRequestedBy);
        Assert.Null(game.Apply(Duel.Attacker, swapped.Purchases.First()));
        Assert.False(game.AskSwitch(Duel.Attacker));
    }

    [Fact]
    public void Finished_practice_retires_after_the_human_disconnects() {
        var game = new Duel();
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out var chair));
        game.SeatBot(Duel.Attacker);
        for (int wave = 1; !game.Done; wave++) {
            Assert.Null(game.Apply(Duel.Defender, new(wave, "ready", null)));
            Assert.Null(game.Apply(Duel.Attacker, new(wave, "ready", null)));
        }
        Assert.False(game.ShouldRetire(DateTime.UtcNow));
        Assert.True(game.ShouldRetire(chair.LastSeen + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Failed_bot_inference_readies_the_partial_turn_without_rebuying() {
        var game = new Duel();
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        game.SeatBot(Duel.Attacker);
        int calls = 0;
        var result = BotRunner.Turn(game, Duel.Attacker, (view, _, _) => ++calls == 1
            ? view.Purchases.First() : throw new InvalidOperationException("Model unavailable"));
        Assert.Single(result.Purchases);
        Assert.False(result.ReadyRefused);
        Assert.False(game.BotToMove);
        Assert.Null(game.Apply(Duel.Defender, new(1, "ready", null)));
        Assert.Single(Assert.Single(game.ObservePlanning(Duel.Defender).WaveHistory).Attack);
    }

    [Fact]
    public void Disconnected_practice_can_be_resumed_after_the_pvp_grace_period() {
        var game = new Duel();
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out var chair));
        game.SeatBot(Duel.Attacker);
        var now = DateTime.UtcNow;
        chair.LastSeen = now - TimeSpan.FromMinutes(2);
        Assert.False(game.ShouldRetire(now));
        chair.LastSeen = now - TimeSpan.FromMinutes(11);
        Assert.True(game.ShouldRetire(now));
    }
}
