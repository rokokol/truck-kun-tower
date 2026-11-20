using System.Reflection;
using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

// The numbers a player reads on an order card and the rules they feel in a wave: a price, the
// keep's hearts, a limit on a rune, the rate of a tower, a refund, and a shot that leads.
public sealed class BalanceRuleTests {
    static readonly int[] Road = [64, 65, 66, 67, 52, 53, 54, 69, 84, 83, 82];

    [Fact]
    public void A_mine_costs_three_and_the_keep_holds_ten_hearts() {
        var board = Board.Open(Road);

        Assert.Equal(3, board.Price("mine"));
        Assert.Equal(10, board.Keep);
    }

    [Fact]
    public void The_rune_strikes_three_weakest_targets_not_the_whole_crowd() {
        var board = Board.Open(Road);
        board.Build("pulse", board.Beside(Road[1]));
        board.Order("swarm", count: 1);

        var frames = board.Play();
        var full = frames.MaxBy(frame => frame.Shots.Length)!;

        Assert.Equal(3, full.Shots.Length);
        Assert.Equal(3, full.Shots.Select(shot => shot.TargetCell).Distinct().Count());
        Assert.All(frames, frame => Assert.InRange(frame.Shots.Length, 0, 3));
    }

    [Fact]
    public void A_bolt_fires_more_often_than_a_cannon_but_less_than_every_other_tick() {
        var board = Board.Open(Road);
        board.Build("bolt", board.Beside(Road[1]));
        board.Build("cannon", board.Beside(Road[2]));
        board.Order("giant");

        var frames = board.Play();
        int bolt = frames.Sum(frame => frame.Shots.Count(shot => shot.Cell == board.Site("bolt")));
        int cannon = frames.Sum(frame => frame.Shots.Count(shot => shot.Cell == board.Site("cannon")));

        Assert.True(bolt > cannon, $"bolt {bolt} must fire more often than cannon {cannon}");
        Assert.True(bolt < frames.Length / 2, "the bolt is slower than one shot every other tick");
    }

    [Fact]
    public void Unbuilding_returns_the_gold_and_the_attacker_cannot_do_it() {
        var board = Board.Open(Road);
        int before = board.Gold(Duel.Defender);
        board.Build("fence", Road[1]);
        int afterFence = board.Gold(Duel.Defender);
        Assert.True(afterFence < before);

        board.Unbuild(Road[1]);

        Assert.Equal(before, board.Gold(Duel.Defender));
        Assert.False(board.HasFence(Road[1]));
        Assert.NotNull(board.AttackerUnbuild(Road[1]));

        board.Build("bolt", board.Beside(Road[1]));
        int afterTower = board.Gold(Duel.Defender);
        board.Unbuild(board.Site("bolt"));
        Assert.True(board.Gold(Duel.Defender) > afterTower);
        Assert.Empty(board.Towers());
    }

    [Fact]
    public void A_shot_marks_the_cell_the_struck_slime_stands_on() {
        var board = Board.Open(Road);
        board.Build("cannon", board.Beside(Road[2]));
        board.Order("swarm", count: 1);

        var frames = board.Play();
        var cell = board.Site("cannon");
        var aimed = frames.SelectMany(frame => frame.Shots
            .Where(shot => shot.Cell == cell)
            .Select(shot => (shot, aim: frame.Aims.SingleOrDefault(a => a.Cell == cell)))).ToArray();

        Assert.NotEmpty(aimed);
        // The board draws a sight line where the tower looked this tick, so the shot and the aim
        // must name the same cell: a slime's own cell, never a guess at where it will be
        Assert.All(aimed, pair => Assert.Equal(pair.aim.TargetCell, pair.shot.TargetCell));
    }

    sealed record Mob(int X, int Y, string Kind, int Id) {
        public int Cell => Y * Maze.Width + X;
    }
    sealed record Target(int Cell, int X, int Y) {
        public int TargetCell => Y * Maze.Width + X;
    }
    sealed record Building(int Cell, int Hp, int Max);
    sealed record Fence(int Cell);
    sealed record Frame(int Hp, Mob[] Mobs, Target[] Shots, Target[] Aims, Fence[] Fences, Building[] Buildings) {
        public bool HasFence(int cell) => Fences.Any(fence => fence.Cell == cell);
    }

    sealed class Board {
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        readonly Duel duel = new();
        int wave = 1;
        int[] path = [];
        readonly Dictionary<string, int> sites = new();

        public static Board Open(int[] road) {
            var board = new Board();
            board.path = road;
            var maze = Field<Maze>(board.duel, "maze");
            maze.Routes.Clear();
            maze.RouteOptions.Clear();
            maze.Routes.Add(road[0], road);
            maze.RouteOptions.Add(road[0], [road]);
            var corridor = Field<HashSet<int>>(maze, "corridor");
            corridor.Clear();
            corridor.UnionWith(road);
            typeof(Maze).GetField("<FenceCells>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(maze, road.Where(maze.CanFence).Order().ToArray());
            Assert.True(board.duel.TryTake(PlayerRef.Nobody, Duel.Defender, out _));
            Assert.True(board.duel.TryTake(PlayerRef.Nobody, Duel.Attacker, out _));
            var purse = Field<int[]>(board.duel, "gold");
            purse[Duel.Defender] = purse[Duel.Attacker] = 60;
            return board;
        }

        public int Keep => View().GetProperty("maxHp").GetInt32();
        public int Price(string action) => View().GetProperty("prices").GetProperty(action).GetInt32();
        public int Gold(int side) => Field<int[]>(duel, "gold")[side];
        public int Site(string type) => sites[type];
        public IReadOnlyList<Building> Towers() =>
            View().GetProperty("towers").Deserialize<Building[]>(Json)!;

        public int Beside(int roadCell) {
            var maze = Field<Maze>(duel, "maze");
            return new[] { roadCell - 1, roadCell + 1, roadCell - Maze.Width, roadCell + Maze.Width }
                .First(maze.CanBuild);
        }

        public void Build(string type, int cell) {
            Assert.Null(duel.Apply(Duel.Defender, new(wave, type, cell)));
            sites[type] = cell;
        }

        public void Unbuild(int cell) => Assert.Null(duel.Apply(Duel.Defender, new(wave, "unbuild", cell)));
        public object? AttackerUnbuild(int cell) => duel.Apply(Duel.Attacker, new(wave, "unbuild", cell));

        public void Order(string type, int count = 1) {
            for (int n = 0; n < count; n++) Assert.Null(duel.Apply(Duel.Attacker, new(wave, type, path[0])));
        }

        public bool HasFence(int cell) => View().GetProperty("fences").Deserialize<Fence[]>(Json)!
            .Any(fence => fence.Cell == cell);

        public Frame[] Play() {
            Assert.Null(duel.Apply(Duel.Defender, new(wave, "ready", null)));
            Assert.Null(duel.Apply(Duel.Attacker, new(wave, "ready", null)));
            wave++;
            return Frames();
        }

        public Frame[] Frames() => View().GetProperty("frames").Deserialize<Frame[]>(Json)!;

        JsonElement View() => JsonSerializer.SerializeToElement(duel.View(Duel.Defender), Json);

        static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    }
}
