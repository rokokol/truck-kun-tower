using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class MazeAttachmentTests {
    const int Keep = 5 * Maze.Width + 7;

    [Fact]
    public void Every_gate_attaches_to_a_cell_the_tree_grew() {
        for (int seed = 0; seed < 200; seed++) {
            var maze = new Maze(new Random(seed));
            Assert.Equal(3, maze.Routes.Count);
            foreach (var (gate, path) in maze.Routes) {
                Assert.Equal(gate, path[0]);
                Assert.Equal(Keep, path[^1]);
                // The gate cell stands outside the lattice, so the first step must be the odd cell beside it
                int first = path[1];
                Assert.Equal(1, Distance(first, gate));
                Assert.Equal(1, first % Maze.Width % 2);
                Assert.Equal(1, first / Maze.Width % 2);
                Assert.Equal(path.Length, path.Distinct().Count());
                for (int step = 1; step < path.Length; step++)
                    Assert.Equal(1, Distance(path[step], path[step - 1]));
            }
            foreach (var options in maze.RouteOptions.Values)
                foreach (var alternate in options.Skip(1))
                    Assert.Equal(Keep, alternate[^1]);
        }
    }

    static int Distance(int left, int right) => Math.Abs(left % Maze.Width - right % Maze.Width) +
        Math.Abs(left / Maze.Width - right / Maze.Width);
}
