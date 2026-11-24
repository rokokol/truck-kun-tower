using System.Reflection;
using System.Text.Json;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class RangedCombatTests {
    [Fact]
    public void Ranged_walks_three_ticks_per_step_and_spends_ten_shots_at_the_keep() {
        var duel = JoinedDuel();
        var path = LeftPath(duel);
        int keep = path[^1];
        FundAndOrder(duel, path[0]);
        var frames = Fight(duel);

        var first = Assert.Single(Items(frames[0], "mobs"));
        Assert.Equal(path[0], Cell(first));
        Assert.Equal(path[0], Cell(Assert.Single(Items(frames[2], "mobs"))));
        Assert.Equal(path[1], Cell(MobAt(frames[3], Number(first, "id"))));

        var groups = Siege(frames).GroupBy(hit => Number(hit.Shot, "mobId")).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal(Number(frames[0], "hp"), groups.Sum(group => group.Count()));
        Assert.All(groups, group => {
            var hits = group.ToArray();
            Assert.InRange(hits.Length, 1, 10);
            Assert.Equal(2, Distance(Cell(hits[0].Shot), keep));
            Assert.All(hits, hit => {
                Assert.Equal(keep, Number(hit.Shot, "cell"));
                Assert.Equal(Cell(hits[0].Shot), Cell(hit.Shot));
                Assert.Equal(Cell(hit.Shot), Cell(MobAt(frames[hit.Tick], group.Key)));
            });
        });
        // Ten shots of one against the keep finish it, so the wave ends on the last of them
        Assert.Equal(0, Number(frames[^1], "hp"));
    }

    [Fact]
    public void Ranged_breaks_fences_and_retargets_without_a_step_or_ammo_refill() {
        var duel = JoinedDuel();
        var path = LeftPath(duel);
        Buy(duel, Duel.Defender, "fence", path[1]);
        Buy(duel, Duel.Defender, "fence", path[2]);
        var planned = Items(View(duel, Duel.Defender), "fences");
        FundAndOrder(duel, path[0]);
        var frames = Fight(duel);

        var firstId = Number(Assert.Single(Items(frames[0], "mobs")), "id");
        var hits = Siege(frames).Where(hit => Number(hit.Shot, "mobId") == firstId).ToArray();
        Assert.Equal(10, hits.Length);
        Assert.Equal(new[] { path[1], path[2], path[^1] },
            hits.Select(hit => Number(hit.Shot, "cell")).Distinct());
        int removed = Array.FindIndex(frames, frame =>
            !Items(frame, "fences").Any(fence => Number(fence, "cell") == path[2]));
        Assert.True(removed > 0);
        Assert.All(Enumerable.Range(0, removed + 1), tick =>
            Assert.Equal(path[0], Cell(MobAt(frames[tick], firstId))));
        Assert.Equal(path[1], Cell(MobAt(frames[removed + 1], firstId)));
        Assert.Equal(path[2], Number(Assert.Single(Items(frames[removed], "siege"), shot =>
            Number(shot, "mobId") == firstId), "cell"));

        Assert.All(planned, fence => {
            Assert.True(Number(fence, "hp") > 12);
            Assert.Equal(Number(fence, "hp"), Number(fence, "max"));
        });
        Assert.All(frames.SelectMany(frame => Items(frame, "fences")), fence => {
            var initial = Assert.Single(planned, f => Number(f, "cell") == Number(fence, "cell"));
            Assert.Equal(Number(initial, "hp"), Number(fence, "max"));
            Assert.InRange(Number(fence, "hp"), 1, Number(fence, "max"));
        });
    }

    [Fact]
    public void One_ranged_breaks_a_tower_then_leaves_without_the_second_ranged() {
        var duel = JoinedDuel();
        var path = LeftPath(duel);
        int tower = path[0] + Maze.Width;
        Buy(duel, Duel.Defender, "frost", tower);
        FundAndOrder(duel, path[0]);
        var frames = Fight(duel);

        int gone = Array.FindIndex(frames, frame => Items(frame, "buildings").Length == 0);
        Assert.True(gone > 0);
        Assert.All(frames.Take(gone + 1), frame =>
            Assert.Equal(path[0], Cell(Assert.Single(Items(frame, "mobs")))));
        var hits = Siege(frames).Where(hit => Number(hit.Shot, "cell") == tower).ToArray();
        Assert.InRange(hits.Length, 1, 10);
        Assert.Equal(gone, hits[^1].Tick);
        int gunner = Number(hits[0].Shot, "mobId");
        Assert.Contains(frames.Skip(gone + 1).SelectMany(frame => Items(frame, "mobs")),
            mob => Number(mob, "id") == gunner && Cell(mob) == path[1]);
    }

    [Fact]
    public void Tower_damage_precedes_siege_and_surviving_gunner_uses_its_own_clock() {
        var duel = JoinedDuel();
        int gate = LeftPath(duel)[0];
        int tower = gate + Maze.Width;
        Buy(duel, Duel.Defender, "cannon", tower);
        FundAndOrder(duel, gate);
        var frames = Fight(duel);

        var bolt = Assert.Single(Items(frames[0], "shots"));
        Assert.Equal(tower, Number(bolt, "cell"));
        Assert.Equal(gate, Cell(bolt));
        Assert.False(bolt.TryGetProperty("mobId", out _));
        Assert.Empty(Items(frames[0], "mobs"));
        Assert.Empty(Items(frames[0], "siege"));
        var shots = Siege(frames).ToArray();
        Assert.Equal(1, shots[0].Tick);
        Assert.All(shots, hit => Assert.Equal(Cell(hit.Shot),
            Cell(MobAt(frames[hit.Tick], Number(hit.Shot, "mobId")))));
    }

    [Fact]
    public void A_single_cannon_hit_kills_a_full_health_gunner_in_the_last_wave() {
        var duel = JoinedDuel();
        for (int wave = 1; wave < 5; wave++) Fight(duel, wave);

        int gate = LeftPath(duel)[0];
        int tower = gate + Maze.Width;
        Buy(duel, Duel.Defender, "cannon", tower, wave: 5);
        Buy(duel, Duel.Attacker, "ranged", gate, wave: 5);
        var firstFrame = Fight(duel, wave: 5)[0];

        Assert.Equal(tower, Number(Assert.Single(Items(firstFrame, "shots")), "cell"));
        Assert.Empty(Items(firstFrame, "mobs"));
        Assert.Empty(Items(firstFrame, "siege"));
    }

    [Fact]
    public void Replay_mob_metadata_distinguishes_the_carrier_and_its_passengers() {
        var duel = JoinedDuel();
        FundAndOrder(duel, LeftPath(duel)[0], "apc");
        var frames = Fight(duel);

        var carrier = Assert.Single(Items(frames[0], "mobs"));
        Assert.Equal("apc", carrier.GetProperty("kind").GetString());
        int carrierId = Number(carrier, "id");
        var passengerIds = new HashSet<int>();
        foreach (var frame in frames) {
            var mobs = Items(frame, "mobs");
            Assert.Equal(mobs.Length, mobs.Select(mob => Number(mob, "id")).Distinct().Count());
            foreach (var mob in mobs) {
                int id = Number(mob, "id");
                Assert.True(id > 0);
                Assert.True(Number(mob, "max") > 0);
                Assert.Equal(Number(mob, "max"), Number(mob, "hp"));
                Assert.False(mob.GetProperty("frozen").GetBoolean());
                if (mob.GetProperty("kind").GetString() == "apc") Assert.Equal(carrierId, id);
                else {
                    Assert.NotEqual(carrierId, id);
                    passengerIds.Add(id);
                }
            }
        }
        Assert.NotEmpty(passengerIds);
    }

    static Duel JoinedDuel() {
        var duel = new Duel();
        Assert.True(duel.TryTake(new(null, null), Duel.Defender, out _));
        Assert.True(duel.TryTake(new(null, null), Duel.Attacker, out _));
        return duel;
    }

    static int[] LeftPath(Duel duel) => Items(View(duel), "paths")
        .Select(path => path.EnumerateArray().Select(cell => cell.GetInt32()).ToArray())
        .Single(path => path[0] % Maze.Width == 0);

    static void FundAndOrder(Duel duel, int cell, string type = "ranged") {
        var purse = (int[])typeof(Duel).GetField("gold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(duel)!;
        purse[Duel.Attacker] = Number(View(duel).GetProperty("prices"), type);
        Buy(duel, Duel.Attacker, type, cell);
    }

    static void Buy(Duel duel, int side, string action, int cell, int wave = 1) =>
        Assert.Null(duel.Apply(side, new(wave, action, cell)));

    static JsonElement[] Fight(Duel duel, int wave = 1) {
        Assert.Null(duel.Apply(Duel.Defender, new(wave, "ready", null)));
        Assert.Null(duel.Apply(Duel.Attacker, new(wave, "ready", null)));
        return Items(View(duel), "frames");
    }

    static JsonElement View(Duel duel, int seat = Duel.Attacker) =>
        JsonSerializer.SerializeToElement(duel.View(seat), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    static IEnumerable<(int Tick, JsonElement Shot)> Siege(JsonElement[] frames) =>
        frames.SelectMany((frame, tick) => Items(frame, "siege").Select(shot => (tick, shot)));

    static JsonElement MobAt(JsonElement frame, int id) =>
        Assert.Single(Items(frame, "mobs"), mob => Number(mob, "id") == id);

    static JsonElement[] Items(JsonElement obj, string name) => obj.GetProperty(name).EnumerateArray().ToArray();
    static int Number(JsonElement obj, string name) => obj.GetProperty(name).GetInt32();
    static int Cell(JsonElement point) => Number(point, "x") + Maze.Width * Number(point, "y");
    static int Distance(int a, int b) => Math.Max(Math.Abs(a % Maze.Width - b % Maze.Width),
        Math.Abs(a / Maze.Width - b / Maze.Width));
}
