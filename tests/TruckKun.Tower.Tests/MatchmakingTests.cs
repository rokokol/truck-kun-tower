using System.Reflection;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class MatchmakingTests {
    [Fact]
    public void Only_a_watched_public_room_with_a_free_seat_can_match() {
        var host = new PlayerRef(Guid.NewGuid(), null);
        var guest = new PlayerRef(Guid.NewGuid(), null);
        var game = new Duel { RandomOpponent = true };
        Assert.True(game.TryTake(host, Duel.Defender, out _));
        Assert.False(game.CanMatch);
        Assert.False(game.Watch(guest, "foreign"));
        Assert.True(game.Watch(host, "phone"));
        Assert.True(game.Watch(host, "desktop"));
        Assert.True(game.CanMatch);
        var now = DateTime.UtcNow;
        game.Unwatch("phone", now);
        Assert.True(game.CanMatch);
        game.Unwatch("desktop", now);
        Assert.False(game.CanMatch);
        Assert.False(game.ShouldRetire(now + Duel.DisconnectGrace / 2));
        Assert.True(game.ShouldRetire(now + Duel.DisconnectGrace));
        Assert.True(game.Watch(host, "reconnect"));
        Assert.True(game.CanMatch);
        Assert.True(game.TryTake(guest, Duel.Attacker, out _));
        Assert.False(game.CanMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_idle_or_disconnected_player_ends_the_duel_regardless_of_how_they_joined(bool randomOpponent) {
        var host = new PlayerRef(Guid.NewGuid(), null);
        var guest = new PlayerRef(Guid.NewGuid(), null);
        var game = new Duel { RandomOpponent = randomOpponent };
        Assert.True(game.TryTake(host, Duel.Defender, out _));
        Assert.True(game.TryTake(guest, Duel.Attacker, out _));
        Assert.True(game.Watch(host, "host"));
        Assert.True(game.Watch(guest, "guest"));
        Assert.Null(game.Apply(Duel.Defender, new(1, "ready", null)));
        var deadline = DateTime.UtcNow.AddSeconds(-1);
        typeof(Duel).GetField("deadline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, deadline);
        Assert.True(game.ShouldRetire(DateTime.UtcNow));
        Assert.False(game.CloseIfExpired());
        Assert.Equal(0, game.ResolvedWaves);
        Assert.Null(game.TakeResult());

        var disconnected = DateTime.UtcNow;
        game.Unwatch("guest", disconnected);
        Assert.False(game.ShouldRetire(disconnected + Duel.DisconnectGrace / 2));
        Assert.True(game.ShouldRetire(disconnected + Duel.DisconnectGrace));
    }
}
