using System.Reflection;
using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class CarrierTrapTests {
    static readonly int[] Road = [64, 65, 66, 67, 52, 53, 54, 69, 84, 83, 82];
    // The transport carries five, each hits the keep for two, and a set of spikes holds five charges
    const int Passengers = 5, PassengerDamage = 2, Spikes = 5;

    [Fact]
    public void Carrier_unloads_at_seventy_percent_and_passengers_walk_the_rest() {
        var stage = new Stage([62, 63, ..Road]);
        int stop = (int)Math.Ceiling((stage.Path.Length - 1) * 0.7);
        int[] walk = stage.Path[stop..];
        stage.Order("apc");

        var frames = stage.Play();

        Assert.Equal(stage.Path[..(stop + 1)], Mobs(frames, "apc").Select(mob => mob.Cell).Distinct());
        int first = Array.FindIndex(frames, frame => frame.At("balaclava", walk[0]));
        Assert.True(first > 0);
        Assert.False(frames[first - 1].At("apc", walk[0]));
        Assert.True(frames[first].At("apc", walk[0]));
        Assert.Single(frames[first].Mobs, mob => mob.Kind == "balaclava");
        Assert.Equal(walk[..^1], Mobs(frames, "balaclava").Select(mob => mob.Cell).Distinct());
        int last = Array.FindLastIndex(frames, frame => frame.Mobs.Any(mob => mob.Kind == "apc"));
        Assert.Equal(Passengers, Mobs(frames, "balaclava").Select(mob => mob.Id).Distinct().Count());
        Assert.DoesNotContain(frames.Skip(last + 1), frame => frame.Mobs.Any(mob => mob.Kind == "apc"));
        Assert.Equal(stage.InitialHp - Passengers * PassengerDamage, frames[^1].Hp);
        AssertSingleFile(frames);
    }

    [Fact]
    public void Carrier_breaks_the_first_fence_at_the_gate_and_passengers_stop_at_the_next() {
        var stage = new Stage(Road);
        stage.Build("fence", Road[1]);
        stage.Build("fence", Road[3]);
        stage.Order("apc");

        var frames = stage.Play();

        int broken = Array.FindIndex(frames, frame => !frame.HasFence(Road[1]));
        Assert.True(broken > 0);
        Assert.True(frames[broken - 1].HasFence(Road[1]));
        Assert.True(frames[broken].HasFence(Road[3]));
        Assert.True(frames[broken].At("apc", Road[0]));
        Assert.True(frames[broken].At("balaclava", Road[0]));
        Assert.All(Mobs(frames, "apc"), mob => Assert.Equal(Road[0], mob.Cell));
        Assert.Equal(Road[..^1], Mobs(frames, "balaclava").Select(mob => mob.Cell).Distinct());
        Assert.Equal(stage.InitialHp - Passengers * PassengerDamage, frames[^1].Hp);
        AssertSingleFile(frames);
    }

    [Fact]
    public void A_carrier_that_dies_before_deployment_releases_nothing() {
        var stage = new Stage(Road);
        stage.Fund(30);
        foreach (var (type, cell) in stage.Loadout(Road[0], Road[1])) stage.Build(type, cell);
        stage.Order("apc");

        var frames = stage.Play();

        Assert.NotEmpty(Mobs(frames, "apc"));
        Assert.Empty(Mobs(frames, "balaclava"));
        Assert.Equal(stage.InitialHp, frames[^1].Hp);
    }

    [Fact]
    public void A_carrier_never_releases_a_passenger_after_it_dies() {
        var stage = new Stage(Road);
        stage.Fund(40);
        foreach (var (type, cell) in stage.Loadout(Road[0], Road[1], Road[2])) stage.Build(type, cell);
        stage.Order("apc");

        var frames = stage.Play();

        var seen = new HashSet<int>();
        for (int tick = 0; tick < frames.Length; tick++) {
            bool carrier = frames[tick].Mobs.Any(mob => mob.Kind == "apc");
            foreach (var passenger in frames[tick].Mobs.Where(mob => mob.Kind == "balaclava")) {
                if (!seen.Add(passenger.Id)) continue;
                // A release happens on the tick the carrier is still alive, never after its death
                Assert.True(carrier, $"passenger {passenger.Id} appeared after the carrier was gone");
            }
            Assert.True(seen.Count <= Passengers);
        }
        Assert.True(seen.Count < Passengers, "a strong defence stops the unload early");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void At_the_end_of_a_short_route_only_passengers_hit_the_keep(int length) {
        var stage = new Stage(Road[^length..]);
        stage.Order("apc");

        var frames = stage.Play();

        Assert.Equal(Enumerable.Range(0, Passengers + 1).Select(n => n * PassengerDamage),
            frames.Select(frame => stage.InitialHp - frame.Hp).Prepend(0).Distinct());
        Assert.Empty(frames[^1].Mobs);
    }

    [Fact]
    public void A_giant_waiting_on_spikes_only_spends_one_charge() {
        var stage = new Stage(Road);
        stage.Build("spikes", Road[1]);
        stage.Build("fence", Road[2]);
        stage.Order("giant");

        var frames = stage.Play();

        Assert.True(frames.Count(frame => frame.At("giant", Road[1])) > 5);
        var hit = Assert.Single(frames, frame => frame.Shots.Any(shot => shot.Cell == Road[1]));
        Assert.True(hit.At("giant", Road[1]));
        Assert.Equal((Spikes - 1, Spikes), frames[^1].Trap(Road[1]));
    }

    [Fact]
    public void The_last_charge_kills_a_weak_slime_and_still_emits_a_shot() {
        var stage = new Stage(Road);
        stage.Build("spikes", Road[2]);
        stage.Order("swarm");
        var first = stage.Play();

        var hits = first.Where(frame => frame.Shots.Any(shot => shot.Cell == Road[2])).ToArray();
        Assert.Equal(Spikes, hits.Length);
        Assert.All(hits, frame => {
            Assert.Equal(Road[2], Assert.Single(frame.Shots).TargetCell);
            Assert.False(frame.At("swarm", Road[2]));
        });
        Assert.Null(hits[^1].Trap(Road[2]));
        Assert.Equal(stage.InitialHp, first[^1].Hp);
    }

    [Fact]
    public void A_carrier_crosses_spikes_without_a_hit_or_a_charge_spent() {
        var stage = new Stage(Road);
        stage.Build("spikes", Road[1]);
        stage.Order("apc");

        var frames = stage.Play();

        Assert.Contains(frames, frame => frame.At("apc", Road[1]));
        Assert.Empty(frames.SelectMany(frame => frame.Shots));
        Assert.Equal((Spikes, Spikes), frames[^1].Trap(Road[1]));
        Assert.Equal(stage.InitialHp - Passengers * PassengerDamage, frames[^1].Hp);
    }

    [Fact]
    public void Spikes_hit_each_passenger_as_they_enter_the_unload_tile_but_not_the_carrier() {
        var stage = new Stage(Road);
        stage.Build("spikes", Road[7]);
        stage.Order("apc");

        var frames = stage.Play();

        int first = Array.FindIndex(frames, frame => frame.At("apc", Road[7]));
        Assert.True(first >= 0);
        Assert.Single(frames[first].Shots);
        Assert.Equal(Spikes, frames.SelectMany(frame => frame.Shots).Count(shot => shot.Cell == Road[7]));
        Assert.Empty(Mobs(frames, "balaclava"));
        Assert.Null(frames[^1].Trap(Road[7]));
        Assert.Equal(stage.InitialHp, frames[^1].Hp);
    }

    [Theory]
    [InlineData("apc", 7, 0)]
    [InlineData("armored", 2, 2)]
    public void A_mine_kills_only_the_first_entrant_and_emits_one_shot(string kind, int step, int damage) {
        var stage = new Stage(Road);
        int cell = Road[step];
        stage.Build("mine", cell);
        stage.Order(kind);

        var frames = stage.Play();

        var trigger = Assert.Single(frames, frame => frame.Shots.Any(shot => shot.Cell == cell));
        int index = Array.IndexOf(frames, trigger);
        Assert.NotNull(frames[index - 1].Trap(cell));
        Assert.True(frames[index - 1].At(kind, Road[step - 1]));
        Assert.Equal(cell, Assert.Single(trigger.Shots).TargetCell);
        Assert.Null(trigger.Trap(cell));
        Assert.False(trigger.At(kind, cell));
        Assert.Empty(Mobs(frames, "balaclava"));
        Assert.Equal(stage.InitialHp - damage, frames[^1].Hp);
        if (kind == "apc") {
            stage.Order("swarm");
            var nextWave = stage.Play();
            Assert.Empty(nextWave.SelectMany(frame => frame.Shots));
            Assert.Equal(stage.InitialHp - 5, nextWave[^1].Hp);
        }
    }

    [Fact]
    public void Ranged_damage_can_destroy_spikes_before_the_first_entry() {
        var stage = new Stage(Road);
        stage.Build("fence", Road[1]);
        stage.Build("spikes", Road[2]);
        stage.Order("ranged", fundOrder: true);

        var frames = stage.Play();

        Assert.Contains(frames, frame => frame.Siege.Any(shot => shot.Cell == Road[2]));
        Assert.Null(frames[^1].Trap(Road[2]));
        Assert.DoesNotContain(frames, frame => frame.Shots.Any(shot => shot.Cell == Road[2]));
    }

    static IEnumerable<Mob> Mobs(Frame[] frames, string kind) =>
        frames.SelectMany(frame => frame.Mobs).Where(mob => mob.Kind == kind);

    static void AssertSingleFile(Frame[] frames) => Assert.All(frames, frame => {
        var passengers = frame.Mobs.Where(mob => mob.Kind == "balaclava").Select(mob => mob.Cell).ToArray();
        Assert.Equal(passengers.Length, passengers.Distinct().Count());
    });

    sealed class Stage {
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        readonly Duel duel = new();
        int wave = 1;
        public int[] Path { get; }
        public int InitialHp { get; }

        public Stage(int[] path) {
            Path = path;
            var maze = Field<Maze>(duel, "maze");
            maze.Routes.Clear();
            maze.RouteOptions.Clear();
            maze.Routes.Add(path[0], path);
            maze.RouteOptions.Add(path[0], [path]);
            var corridor = Field<HashSet<int>>(maze, "corridor");
            corridor.Clear();
            corridor.UnionWith(path);
            typeof(Maze).GetField("<FenceCells>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(maze, path.Where(maze.CanFence).Order().ToArray());
            Assert.True(duel.TryTake(new(Guid.NewGuid(), null), Duel.Defender, out _));
            Assert.True(duel.TryTake(new(Guid.NewGuid(), null), Duel.Attacker, out _));
            InitialHp = JsonSerializer.SerializeToElement(duel.View(Duel.Defender), Json).GetProperty("hp").GetInt32();
        }

        // Two cannons and two bolts on buildable cells beside the given road cells, so a test does
        // not depend on where this map happens to allow a tower
        public IEnumerable<(string Type, int Cell)> Loadout(params int[] near) {
            var maze = Field<Maze>(duel, "maze");
            var used = new HashSet<int>();
            string[] types = ["cannon", "cannon", "bolt", "bolt"];
            foreach (var type in types) {
                int cell = near.SelectMany(c => new[] { c - 1, c + 1, c - Maze.Width, c + Maze.Width })
                    .First(c => maze.CanBuild(c) && used.Add(c));
                yield return (type, cell);
            }
        }

        // A test that needs a heavier defence buys one, since a fresh duel starts with ten gold
        public void Fund(int gold) {
            var purse = Field<int[]>(duel, "gold");
            purse[Duel.Defender] = gold;
        }

        public void Build(string type, int cell) => Move(Duel.Defender, type, cell);
        public void Order(string type, bool fundOrder = false) {
            if (fundOrder) {
                var view = JsonSerializer.SerializeToElement(duel.View(Duel.Attacker), Json);
                Field<int[]>(duel, "gold")[Duel.Attacker] = view.GetProperty("prices").GetProperty(type).GetInt32();
            }
            Move(Duel.Attacker, type, Path[0]);
        }

        public Frame[] Play() {
            Move(Duel.Defender, "ready", null);
            Move(Duel.Attacker, "ready", null);
            wave++;
            var view = JsonSerializer.SerializeToElement(duel.View(Duel.Defender), Json);
            return view.GetProperty("frames").Deserialize<Frame[]>(Json)!;
        }

        void Move(int side, string action, int? cell) => Assert.Null(duel.Apply(side, new(wave, action, cell)));

        static T Field<T>(object owner, string name) =>
            (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    }

    sealed record Mob(int X, int Y, string Kind, int Id) {
        public int Cell => Y * Maze.Width + X;
    }
    sealed record Target(int Cell, int X, int Y) {
        public int TargetCell => Y * Maze.Width + X;
    }
    sealed record Fence(int Cell);
    sealed record Building(int Cell, int Hp, int Max);
    sealed record Frame(int Hp, Mob[] Mobs, Target[] Shots, Target[] Siege, Fence[] Fences, Building[] Buildings) {
        public bool At(string kind, int cell) => Mobs.Any(mob => mob.Kind == kind && mob.Cell == cell);
        public bool HasFence(int cell) => Fences.Any(fence => fence.Cell == cell);
        public (int, int)? Trap(int cell) => Buildings.SingleOrDefault(building => building.Cell == cell) is { } trap
            ? (trap.Hp, trap.Max) : null;
    }
}
