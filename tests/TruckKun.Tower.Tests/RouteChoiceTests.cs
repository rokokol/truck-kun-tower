using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class RouteChoiceTests {
    [Theory]
    [InlineData(0.59, true)]
    [InlineData(0.61, false)]
    public void A_shorter_branch_has_more_weight(double roll, bool shortWay) {
        int[] shortPath = [0, 1, 9];
        int[] longPath = [0, 3, 4, 9];
        Assert.Same(shortWay ? shortPath : longPath, Maze.PickRoute([shortPath, longPath], () => roll));
    }

    [Theory]
    [InlineData(0.49, true)]
    [InlineData(0.51, false)]
    public void Equal_distances_split_the_chance_evenly(double roll, bool first) {
        int[] left = [0, 1, 6];
        int[] right = [0, 5, 6];
        Assert.Same(first ? left : right, Maze.PickRoute([left, right], () => roll));
    }

    [Fact]
    public void A_second_fork_gets_its_own_roll_without_inflating_the_first_branch() {
        int[] direct = [0, 1, 2, 3, 9];
        int[] left = [0, 1, 4, 5, 9];
        int[] right = [0, 8, 10, 11, 9];
        Assert.Same(right, Maze.PickRoute([direct, left, right], () => 0.51));
        Queue<double> rolls = new([0.49, 0.51]);
        Assert.Same(left, Maze.PickRoute([direct, left, right], rolls.Dequeue));
        Assert.Empty(rolls);
    }
}
