using System.Reflection;
using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

// The numbers a player reads on an order card and the rules they feel in a wave: a price, the
// keep's hearts, area damage, shot intervals and refunds.
public sealed class BalanceRuleTests {
    static readonly int[] Road = [64, 65, 66, 67, 52, 53, 54, 69, 84, 83, 82];

    [Fact]
    public void A_mine_costs_three_and_the_keep_holds_ten_hearts() {
        var board = Board.Open(Road);

        Assert.Equal(3, board.Price("mine"));
        Assert.Equal(10, board.Keep);
    }

    [Fact]
    public void The_rune_hits_more_than_three_targets_in_its_zone() {
        int[] path = [64, 65, 66, 67, 68, 69, 70, 71];
        var board = Board.Open(path);
        board.Build("pulse", path[4] + Maze.Width);
        board.Build("fence", path[6]);
        board.Order("armored", count: 2);
        Assert.Contains(board.Play(), frame => frame.Shots.Length > 3);
    }

    [Fact]
    public void A_rune_hit_does_one_damage() {
        var board = Board.Open(Road);
        board.Build("pulse", board.Beside(Road[0]));
        board.Order("giant");
        var giant = Assert.Single(board.Play()[0].Mobs);
        Assert.Equal(1, giant.Max - giant.Hp);
    }

    [Fact]
    public void Shooting_zones_round_large_corners_and_the_bolt_reaches_farther_than_the_cannon() {
        var board = Board.Open([64, 65, 66, 67, 68, 69, 70, 71]);
        Assert.Equal(new[] { 1, 1, 1 }, board.Range("frost"));
        Assert.Equal(new[] { 1, 2, 2, 2, 1 }, board.Range("cannon"));
        Assert.Equal(new[] { 1, 2, 3, 3, 3, 2, 1 }, board.Range("bolt"));
        int bolt = 67 - Maze.Width, cannon = 67 + Maze.Width;
        board.Build("bolt", bolt);
        board.Build("cannon", cannon);
        board.Order("giant");
        var shots = board.Play()[0].Shots;
        Assert.Contains(shots, shot => shot.Cell == bolt && shot.TargetCell == 64);
        Assert.DoesNotContain(shots, shot => shot.Cell == cannon);
    }

    [Fact]
    public void A_bolt_fires_more_often_than_a_cannon_but_less_than_every_other_tick() {
        int bolt = ShotInterval("bolt"), cannon = ShotInterval("cannon");
        Assert.True(bolt < cannon, $"bolt interval {bolt} must be shorter than cannon interval {cannon}");
        Assert.True(bolt > 2, "the bolt is slower than one shot every other tick");

        static int ShotInterval(string type) {
            var board = Board.Open(Road);
            var site = board.Beside(Road[0]);
            board.Build(type, site);
            board.Build("fence", Road[1]);
            board.Order("giant");
            var ticks = board.Play().Select((frame, tick) => (frame, tick))
                .Where(pair => pair.frame.Shots.Any(shot => shot.Cell == site))
                .Select(pair => pair.tick).Take(2).ToArray();
            Assert.Equal(2, ticks.Length);
            return ticks[1] - ticks[0];
        }
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
    public void Unspent_gold_is_replaced_by_the_next_wave_budget() {
        var board = Board.Open(Road);
        foreach (var (defender, attacker) in new[] { (12, 17), (14, 20), (16, 24), (18, 29) }) {
            board.Play();
            Assert.Equal(defender, board.Gold(Duel.Defender));
            Assert.Equal(attacker, board.Gold(Duel.Attacker));
        }
    }

    [Fact]
    public void Demolishing_a_tower_or_fence_from_a_previous_wave_does_not_refund_gold() {
        var board = Board.Open(Road);
        var tower = board.Beside(Road[1]);
        board.Build("bolt", tower);
        board.Build("fence", Road[1]);
        board.Play();
        var budget = board.Gold(Duel.Defender);
        board.Unbuild(tower);
        board.Unbuild(Road[1]);
        Assert.Equal(budget, board.Gold(Duel.Defender));
        Assert.Empty(board.Towers());
        Assert.False(board.HasFence(Road[1]));
        board.Build("bolt", tower);
        board.Unbuild(tower);
        Assert.Equal(budget, board.Gold(Duel.Defender));
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

    sealed record Mob(int X, int Y, string Kind, int Id, int Hp, int Max) {
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
        public int[] Range(string action) => View().GetProperty("attackRows").GetProperty(action).Deserialize<int[]>(Json)!;
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
