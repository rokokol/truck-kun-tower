using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class PlanningTests {
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    static Duel OpenBoard() {
        var game = new Duel();
        Assert.True(game.TryTake(PlayerRef.Nobody, Duel.Defender, out _));
        Assert.True(game.TryTake(PlayerRef.Nobody, Duel.Attacker, out _));
        return game;
    }

    static JsonElement View(Duel game, int seat) => JsonSerializer.SerializeToElement(game.View(seat), WebJson);
    static int[] Cells(JsonElement row) => row.EnumerateArray().Select(c => c.GetInt32()).ToArray();

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void Either_side_can_confirm_an_empty_wave(int first) {
        var game = OpenBoard();
        var initial = View(game, first);
        Assert.Null(game.Apply(first, new Move(1, "ready", null)));
        var waiting = View(game, first);
        Assert.True(waiting.GetProperty("locked").GetBoolean());
        Assert.Equal(0, waiting.GetProperty("last").GetInt32());
        Assert.Null(game.Apply(1 - first, new Move(1, "ready", null)));
        var next = View(game, first);
        Assert.Equal(1, next.GetProperty("last").GetInt32());
        Assert.False(next.GetProperty("locked").GetBoolean());
        Assert.Equal(initial.GetProperty("hp").GetInt32(), next.GetProperty("hp").GetInt32());
        Assert.Equal(0, next.GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public void A_removed_unit_cannot_be_ordered_and_spends_no_gold() {
        var game = OpenBoard();
        var before = View(game, Duel.Attacker);
        int gate = Cells(before.GetProperty("paths")[0])[0];
        Assert.NotNull(game.Apply(Duel.Attacker, new Move(1, "decoy", gate)));
        var after = View(game, Duel.Attacker);
        Assert.Equal(before.GetProperty("gold").GetInt32(), after.GetProperty("gold").GetInt32());
        Assert.Equal(0, after.GetProperty("orders").GetArrayLength());
        Assert.False(after.GetProperty("prices").TryGetProperty("decoy", out _));
        Assert.Null(game.Apply(Duel.Attacker, new Move(1, "swarm", gate)));
    }

    [Theory]
    [InlineData("spikes", "mine")]
    [InlineData("mine", "spikes")]
    public void Trap_sites_exclude_occupied_routes_and_fences_but_allow_independent_paths(string type, string other) {
        var game = OpenBoard();
        var board = View(game, Duel.Defender);
        var routes = new[] { "paths", "alternatePaths" }
            .SelectMany(key => board.GetProperty(key).EnumerateArray()).Select(Cells).ToArray();
        var road = Cells(board.GetProperty("fenceCells"));
        int site = routes[0][1];
        var blocked = routes.Where(path => path.Contains(site)).SelectMany(path => path).ToHashSet();

        Assert.Contains(site, Cells(board.GetProperty("trapCells")));
        Assert.Null(game.Apply(Duel.Defender, new Move(1, type, site)));
        var remaining = Cells(View(game, Duel.Defender).GetProperty("trapCells"));
        Assert.NotEmpty(remaining);
        Assert.Equal(road.Where(c => !blocked.Contains(c)), remaining);
        Assert.NotNull(game.Apply(Duel.Defender, new Move(1, other, routes[0][2])));
        Assert.NotNull(game.Apply(Duel.Defender, new Move(1, "fence", site)));
        Assert.Null(game.Apply(Duel.Defender, new Move(1, "fence", remaining[0])));
        Assert.DoesNotContain(remaining[0], Cells(View(game, Duel.Defender).GetProperty("trapCells")));
        Assert.NotNull(game.Apply(Duel.Defender, new Move(1, "spikes", remaining[0])));
    }
}
