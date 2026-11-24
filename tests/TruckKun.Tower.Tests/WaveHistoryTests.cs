using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class WaveHistoryTests {
    [Fact]
    public void Rebuilding_the_same_type_on_the_same_site_is_not_a_battle_loss() {
        var game = new Duel(new Maze(new Random(59)), 91);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        var tower = game.ObservePlanning(Duel.Defender).Purchases.First(move => move.Action == "bolt");
        for (int wave = 1; wave <= 2; wave++) {
            if (wave == 2) Assert.Null(game.Apply(Duel.Defender, new(wave, "unbuild", tower.Cell)));
            Assert.Null(game.Apply(Duel.Defender, tower with { Wave = wave }));
            Assert.Null(game.Apply(Duel.Defender, new(wave, "ready", null)));
            Assert.Null(game.Apply(Duel.Attacker, new(wave, "ready", null)));
        }
        var summary = game.ObservePlanning(Duel.Attacker).WaveHistory[^1];
        Assert.Equal(new Duel.Site("bolt", tower.Cell!.Value), Assert.Single(summary.Added));
        Assert.Equal(Assert.Single(summary.Added), Assert.Single(summary.Removed));
        Assert.Empty(summary.Lost);
    }

    [Fact]
    public void Only_committed_groups_and_fielded_defenses_enter_public_history() {
        int[] path = [75,76,77,78,79,80,81,82];
        var game = new Duel(new Maze([[path]]), 1);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        void Buy(int side, string action, int? cell) => Assert.Null(game.Apply(side, new(game.ResolvedWaves + 1, action, cell)));
        void Ready() { Buy(Duel.Defender, "ready", null); Buy(Duel.Attacker, "ready", null); }
        Buy(Duel.Defender, "bolt", 62);
        Buy(Duel.Defender, "unbuild", 62);
        Buy(Duel.Defender, "mine", 76);
        Buy(Duel.Defender, "fence", 80);
        Buy(Duel.Attacker, "swarm", 75);
        Buy(Duel.Attacker, "cancel", 0);
        Buy(Duel.Attacker, "giant", 75);
        foreach (int seat in new[] { Duel.Attacker, Duel.Defender }) Assert.Empty(game.ObservePlanning(seat).WaveHistory);
        Ready();
        var first = Assert.Single(game.ObservePlanning(Duel.Defender).WaveHistory);
        var group = Assert.Single(first.Attack);
        Assert.Equal(("giant", 75, 1), (group.Type, group.Cell, group.Count));
        Assert.Equal(new[] { "mine", "fence" }, first.Added.OrderBy(p => p.Cell).Select(p => p.Type));
        Assert.Empty(first.Removed);
        Assert.Equal("mine", Assert.Single(first.Lost).Type);
        Buy(Duel.Defender, "unbuild", 80);
        Buy(Duel.Attacker, "runner", 75);
        Buy(Duel.Attacker, "runner", 75);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (int seat in new[] { Duel.Attacker, Duel.Defender }) {
            var bot = game.ObservePlanning(seat).WaveHistory;
            Assert.Equal(first, Assert.Single(bot));
            Assert.Equal(JsonSerializer.Serialize(bot, options), JsonSerializer.SerializeToElement(game.View(seat), options).GetProperty("waveHistory").GetRawText());
        }
        Ready();
        var second = game.ObservePlanning(Duel.Attacker).WaveHistory[1];
        Assert.Equal(2, Assert.Single(second.Attack).Count);
        Assert.Equal("fence", Assert.Single(second.Removed).Type);
        Assert.Empty(second.Lost);
        while (!game.Done) Ready();
        Assert.Equal(game.ResolvedWaves, game.ObservePlanning(Duel.Defender).WaveHistory.Length);
        Buy(Duel.Defender, "rematch", null);
        Assert.Empty(game.ObservePlanning(Duel.Attacker).WaveHistory);
    }
}
