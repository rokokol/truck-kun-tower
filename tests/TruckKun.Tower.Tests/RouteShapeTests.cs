using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class RouteShapeTests {
    [Fact]
    public void A_shaped_network_sees_route_length_and_an_older_one_still_loads() {
        Assert.Equal(BotBrain.Length, BotBrain.Create(new Random(1)).Length);
        Assert.Equal(BotBrain.UnshapedLength + 8 * 3, BotBrain.Length);
        var shortView = Game(new[] { 8, 10 }).ObservePlanning(Duel.Attacker);
        var longView = Game(new[] { 24, 26 }).ObservePlanning(Duel.Attacker);
        var shortRoute = BotBrain.RouteVector(shortView);
        var longRoute = BotBrain.RouteVector(longView);
        Assert.Equal(3, shortRoute.Length);
        Assert.True(longRoute[0] > shortRoute[0], $"shortest {shortRoute[0]} vs {longRoute[0]}");
        Assert.True(longRoute[1] > shortRoute[1]);
        Assert.InRange(shortRoute[0], 0.2, 1.0);
        Assert.All(shortRoute, value => Assert.InRange(value, 0.0, 1.0));
        Assert.Equal(BotBrain.UnshapedLength, new double[BotBrain.UnshapedLength].Length);
        var legacy = new Duel(new Maze(new Random(73)), 43);
        Assert.True(legacy.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(legacy.TryTake(new(null, null), Duel.Attacker, out _));
        Assert.NotNull(BotBrain.Pick(legacy.ObservePlanning(Duel.Attacker), new double[BotBrain.UnshapedLength], Duel.Attacker));
    }

    static Duel Game(int[] lengths) {
        var routes = lengths.Select((length, index) => {
            int row = 1 + index * 2;
            var cells = new List<int>();
            for (int i = 0; i < length; i++) cells.Add(row * Maze.Width + Math.Min(Maze.Width - 2, 1 + i));
            cells.Add(5 * Maze.Width + 7);
            return cells.ToArray();
        }).ToArray();
        var maze = new Maze(routes.Select(route => new[] { route }).ToArray());
        var game = new Duel(maze, 5);
        Assert.True(game.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(game.TryTake(new(null, null), Duel.Attacker, out _));
        return game;
    }
}
