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
    public void The_rune_hits_at_most_three_targets_in_its_zone() {
        int[] path = [64, 65, 66, 67, 68, 69, 70, 71];
        var board = Board.Open(path);
        int rune = path[4] + Maze.Width;
        board.Build("pulse", rune);
        board.Build("fence", path[6]);
        board.Order("armored", count: 2);
        var salvos = board.Play().Select(frame => frame.Shots.Count(shot => shot.Cell == rune)).ToArray();
        Assert.Contains(3, salvos);
        Assert.All(salvos, hits => Assert.InRange(hits, 0, 3));
    }

    [Fact]
    public void The_rune_prefers_nearby_slimes_over_the_one_closest_to_the_keep() {
        int[] path = [64, 65, 66, 67, 68, 69, 70, 71];
        var board = Board.Open(path);
        board.Play();
        board.Play();
        int rune = path[1] + Maze.Width;
        board.Build("pulse", rune);
        board.Order("swarm", count: 2);

        var frame = board.Play().First(frame => path.Take(4).All(cell => frame.Mobs.Any(mob => mob.Cell == cell))
            && frame.Shots.Any(shot => shot.Cell == rune));
        Assert.Equal(path.Take(3), frame.Shots.Where(shot => shot.Cell == rune).Select(shot => shot.TargetCell).Order());
        Assert.Equal(path[1], Assert.Single(frame.Aims, aim => aim.Cell == rune).TargetCell);
    }

    [Theory]
    [InlineData(1, 26)]
    [InlineData(2, 28)]
    [InlineData(3, 30)]
    [InlineData(4, 32)]
    [InlineData(5, 34)]
    public void A_giant_has_reduced_health_and_deals_three_damage_to_the_keep(int wave, int health) {
        var board = Board.Open(Road);
        for (int i = 1; i < wave; i++) board.Play();
        board.Order("giant");
        var fight = board.Play();
        Assert.Equal(health, Assert.Single(fight[0].Mobs).Hp);
        Assert.Equal(board.Keep - 3, fight[^1].Hp);
    }

    [Fact]
    public void The_next_attack_budget_counts_only_surviving_defenses() {
        var board = Board.Open(Road);
        board.Build("bolt", board.Beside(Road[1]));
        board.Build("fence", Road[2]);
        board.Build("mine", Road[1]);
        board.Order("giant");

        foreach (int budget in new[] { 15, 18, 24, 25 }) {
            board.Play();
            Assert.Equal(budget, board.Gold(Duel.Attacker));
        }
    }

    [Theory]
    [InlineData("swarm")]
    [InlineData("smart")]
    public void Common_slimes_move_faster_without_matching_runners(string kind) {
        var board = Board.Open(Road);
        board.Order(kind);
        var frames = board.Play();
        int fourthStep = Array.FindIndex(frames, frame => frame.Mobs.Any(mob => mob.Id == 1 && mob.Cell == Road[4]));
        Assert.InRange(fourthStep, 5, 7);

        var guide = JsonSerializer.SerializeToElement(Duel.Guide());
        var unit = guide.GetProperty("units").EnumerateArray().Single(unit => unit.GetProperty("type").GetString() == kind);
        Assert.Equal(fourthStep / 4m, unit.GetProperty("stepTicks").GetDecimal());
    }

    [Fact]
    public void Armored_slimes_stop_next_to_a_tower_and_move_on_after_destroying_it() {
        int[] road = [64, 65, 66, 67, 68, 69, 70, 71];
        var board = Board.Open(road);
        int tower = road[2] + Maze.Width;
        board.Build("frost", tower);
        board.Build("fence", road[4]);
        board.Order("armored");

        var frames = board.Play();
        int firstHit = Array.FindIndex(frames, frame => frame.Buildings.Any(b => b.Cell == tower && b.Hp < b.Max));
        Assert.True(firstHit > 0, "The tower must take a melee hit");
        var damaged = Assert.Single(frames[firstHit].Buildings);
        Assert.Equal(2, damaged.Max - damaged.Hp);
        Assert.Contains(frames[firstHit].Siege, hit => hit.Cell == tower && hit.Melee);
        int destroyed = Array.FindIndex(frames, frame => frame.Buildings.Length == 0);
        Assert.True(destroyed > firstHit);
        Assert.All(frames[firstHit..(destroyed + 1)], frame =>
            Assert.Equal(road[2], frame.Mobs.Single(mob => mob.Id == 1).Cell));
        Assert.Contains(frames[(destroyed + 1)..], frame => frame.Mobs.Any(mob => mob.Id == 1 && mob.Cell == road[4]));
        var firstFenceHits = frames.First(frame => frame.Siege.Any(hit => hit.Melee && hit.Cell == road[4]));
        var damagedFence = Assert.Single(firstFenceHits.Fences);
        Assert.Equal(2 * firstFenceHits.Siege.Count(hit => hit.Melee && hit.Cell == road[4]),
            damagedFence.Max - damagedFence.Hp);

        foreach (int structure in new[] { tower, road[4] }) {
            var hits = Enumerable.Range(0, frames.Length)
                .Where(tick => frames[tick].Siege.Any(hit => hit.Melee && hit.Cell == structure && hit.MobId == 1))
                .Take(2).ToArray();
            Assert.Equal(2, hits.Length);
            Assert.Equal(2, hits[1] - hits[0]);
        }
    }

    [Fact]
    public void Armored_slimes_move_two_cells_in_five_ticks_on_open_road() {
        var board = Board.Open(Road);
        board.Order("armored");
        var frames = board.Play();
        Assert.Equal(3, FirstTickAt(Road[1]));
        Assert.Equal(5, FirstTickAt(Road[2]));

        int FirstTickAt(int cell) => Array.FindIndex(frames, frame => frame.Mobs.Any(mob => mob.Id == 1 && mob.Cell == cell));
        var armored = JsonSerializer.SerializeToElement(Duel.Guide()).GetProperty("units").EnumerateArray()
            .Single(unit => unit.GetProperty("type").GetString() == "armored");
        Assert.Equal(2, armored.GetProperty("interval").GetInt32());
        Assert.Equal(2.5m, armored.GetProperty("stepTicks").GetDecimal());
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

    [Theory]
    [InlineData("bolt", 4)]
    [InlineData("cannon", 6)]
    public void Towers_fire_at_the_expected_intervals(string type, int ticks) {
        Assert.Equal(ticks, ShotInterval(type));

        static int ShotInterval(string tower) {
            var board = Board.Open(Road);
            var site = board.Beside(Road[0]);
            board.Build(tower, site);
            board.Build("fence", Road[1]);
            board.Order("giant");
            var shots = board.Play().Select((frame, tick) => (frame, tick))
                .Where(pair => pair.frame.Shots.Any(shot => shot.Cell == site))
                .Select(pair => pair.tick).Take(2).ToArray();
            Assert.Equal(2, shots.Length);
            return shots[1] - shots[0];
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
        foreach (var (defender, attacker) in new[] { (12, 13), (14, 16), (16, 20), (18, 25) }) {
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
    sealed record Fence(int Cell, int Hp, int Max);
    sealed record SiegeHit(int Cell, int MobId, bool Melee);
    sealed record Frame(int Hp, Mob[] Mobs, Target[] Shots, Target[] Aims, Fence[] Fences, Building[] Buildings, SiegeHit[] Siege) {
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
