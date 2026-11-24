using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class WideMazeTests {
    [Fact]
    public void Most_seeded_maps_offer_a_large_buildable_island() {
        const int samples = 200;
        int wide = 0, small = 0, total = 0;
        HashSet<string> maps = [];
        for (int seed = 0; seed < samples; seed++) {
            var maze = new Maze(new Random(seed));
            var road = maze.RouteOptions.Values.SelectMany(paths => paths).SelectMany(path => path).ToHashSet();
            maps.Add(string.Join(',', road.Order()));
            var unseen = Enumerable.Range(0, Maze.Width * Maze.Height).Where(cell => !road.Contains(cell)).ToHashSet();
            bool foundWide = false;
            while (unseen.Count > 0) {
                int start = unseen.First();
                unseen.Remove(start);
                List<int> region = [start];
                bool touchesEdge = false;
                for (int i = 0; i < region.Count; i++) {
                    int cell = region[i], x = cell % Maze.Width, y = cell / Maze.Width;
                    touchesEdge |= x == 0 || x == Maze.Width - 1 || y == 0 || y == Maze.Height - 1;
                    if (x > 0 && unseen.Remove(cell - 1)) region.Add(cell - 1);
                    if (x + 1 < Maze.Width && unseen.Remove(cell + 1)) region.Add(cell + 1);
                    if (y > 0 && unseen.Remove(cell - Maze.Width)) region.Add(cell - Maze.Width);
                    if (y + 1 < Maze.Height && unseen.Remove(cell + Maze.Width)) region.Add(cell + Maze.Width);
                }
                if (touchesEdge || !region.Any(maze.CanBuild)) continue;
                total++;
                if (region.Count == 1) small++;
                foundWide |= region.Count is >= 4 and <= 6 && region.All(maze.CanBuild);
            }
            if (foundWide) wide++;
            foreach (var (gate, options) in maze.RouteOptions) {
                Assert.Contains(options.Length, new[] { 2, 4 });
                Assert.Equal(maze.Routes[gate], options[0]);
                foreach (var path in options) {
                    Assert.Equal(gate, path[0]);
                    Assert.Equal(options[0][^1], path[^1]);
                    Assert.Equal(path.Length, path.Distinct().Count());
                    Assert.All(path.Zip(path.Skip(1)), edge => Assert.Equal(1,
                        Math.Abs(edge.First % Maze.Width - edge.Second % Maze.Width) +
                        Math.Abs(edge.First / Maze.Width - edge.Second / Maze.Width)));
                }
                Assert.All(options.Skip(1), path => Assert.True(path.Length > options[0].Length));
            }
            Assert.Contains(maze.RouteOptions.Values, paths => paths.Length == 4);
            if (seed is 1 or 45)
                Assert.Equal(Snapshot(maze), Snapshot(new Maze(new Random(seed))));
        }
        Assert.True(wide >= 0.7 * samples, $"Only {wide}/{samples} maps have a large island");
        Assert.True(small * 2 < total, $"{small}/{total} islands contain just one tile");
        Assert.True(maps.Count >= 0.95 * samples, $"Only {maps.Count}/{samples} distinct road masks");
    }

    static string Snapshot(Maze maze) => string.Join(';', maze.RouteOptions.Values.SelectMany(paths => paths)
        .Select(path => string.Join(',', path)));
}
