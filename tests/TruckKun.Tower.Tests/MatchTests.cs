using Moq;
using Xunit;

namespace TruckKun.Tower.Tests;

// History is read far more often than it is written, so the tests check the mapping, the one query
// for nicknames and the two rules that keep a row private to the pair that played it.
public sealed class MatchTests {

    [Fact]
    public async Task Recent_rows_carry_both_nicknames() {
        var stage = new Stage();
        var defender = stage.Account("Защитник");
        var attacker = stage.Account("Атакующий");
        stage.Rows.Add(stage.Finished(defender, attacker, "Defender", 5, 7));

        var list = await stage.Service.RecentAsync(10);

        var match = Assert.Single(list);
        Assert.Equal("Defender", match.Winner);
        Assert.Equal(7, match.HeartsLeft);
        Assert.Equal("Защитник", match.Defender!.Nickname);
        Assert.Equal("Атакующий", match.Attacker!.Nickname);
        stage.Users.Verify(repo => repo.ByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Details_list_the_waves_in_order() {
        var stage = new Stage();
        var defender = stage.Account("Защитник");
        var match = stage.Finished(defender, null, "Defender", 2, 9);
        match.Waves =
        [
            new WaveRow { Number = 2, HeartsAfter = 20, Damage = 4, Slimes = 5, SlimesKilled = 2, Groups = 1 },
            new WaveRow { Number = 1, HeartsAfter = 24, Damage = 0, Slimes = 6, SlimesKilled = 6, Groups = 1 }
        ];
        stage.Rows.Add(match);

        var detail = await stage.Service.GetAsync(match.Id);

        Assert.Equal(2, detail.Waves.Count);
        Assert.Equal(1, detail.Waves[0].Number);
        Assert.Equal(2, detail.Waves[1].Number);
        Assert.Null(detail.Attacker);
        var error = await Assert.ThrowsAsync<AppError>(() => stage.Service.GetAsync(Guid.NewGuid()));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task A_note_belongs_to_a_player_of_that_match() {
        var stage = new Stage();
        var defender = stage.Account("Защитник");
        var attacker = stage.Account("Атакующий");
        var stranger = stage.Account("Чужой");
        var match = stage.Finished(defender, attacker, "Attacker", 5, 0);
        stage.Rows.Add(match);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.SetNoteAsync(stranger, match.Id, "реванш"));

        Assert.Equal(403, error.Status);
        Assert.Equal("", match.Note);
        stage.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_note_is_trimmed_and_capped() {
        var stage = new Stage();
        var defender = stage.Account("Защитник");
        var match = stage.Finished(defender, null, "Defender", 5, 12);
        stage.Rows.Add(match);

        var note = await stage.Service.SetNoteAsync(defender, match.Id, "  реванш  ");
        var long_one = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.SetNoteAsync(defender, match.Id, new string('я', MatchService.MaxNote + 1)));
        var empty = await stage.Service.SetNoteAsync(defender, match.Id, "   ");

        Assert.Equal("реванш", note.Note);
        Assert.Equal(400, long_one.Status);
        Assert.Equal("", empty.Note);
        Assert.Equal("", match.Note);
        stage.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Only_a_player_may_delete_a_match() {
        var stage = new Stage();
        var defender = stage.Account("Защитник");
        var attacker = stage.Account("Атакующий");
        var stranger = stage.Account("Чужой");
        var match = stage.Finished(defender, attacker, "Defender", 5, 8);
        stage.Rows.Add(match);

        var forbidden = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.DeleteAsync(stranger, match.Id));
        await stage.Service.DeleteAsync(defender, match.Id);

        Assert.Equal(403, forbidden.Status);
        Assert.Empty(stage.Rows);
        stage.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
        var missing = await Assert.ThrowsAsync<AppError>(() => stage.Service.DeleteAsync(defender, match.Id));
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task My_matches_are_asked_for_from_the_repository() {
        var stage = new Stage();
        var me = stage.Account("Я");
        var rival = stage.Account("Соперник");
        stage.Rows.Add(stage.Finished(me, rival, "Defender", 5, 10));
        stage.Rows.Add(stage.Finished(rival, me, "Attacker", 4, 3));
        stage.Rows.Add(stage.Finished(rival, stage.Account("Третий"), "Defender", 5, 1));

        var mine = await stage.Service.MineAsync(me, 10);

        Assert.Equal(2, mine.Count);
        Assert.All(mine, match => Assert.Equal("Соперник", (match.Defender!.Id == me ? match.Attacker : match.Defender)!.Nickname));
    }

    [Fact]
    public async Task A_page_size_is_clamped() {
        var stage = new Stage();
        await stage.Service.RecentAsync(500);
        await stage.Service.RecentAsync(0);

        stage.Matches.Verify(repo => repo.RecentAsync(MatchService.MaxTake, It.IsAny<CancellationToken>()), Times.Once);
        stage.Matches.Verify(repo => repo.RecentAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stats_and_the_leaderboard_come_back_as_contracts() {
        var stage = new Stage();
        stage.Account("Первый");
        stage.Account("Второй");

        var stats = await stage.Service.StatsAsync();
        var leaders = await stage.Service.LeaderboardAsync(10);

        Assert.Equal(1, stats.Matches);
        Assert.Equal("Defender", Assert.Single(stats.Sides).Side);
        Assert.Single(stats.Waves);
        Assert.Equal("Первый", leaders[0].Nickname);
        Assert.Equal(2, leaders[0].Wins);
    }

    sealed class Stage {
        public Mock<IUnitOfWork> Unit { get; } = new();
        public Mock<IMatchRepository> Matches { get; } = new();
        public Mock<IUserRepository> Users { get; } = new();
        public List<MatchRow> Rows { get; } = [];
        public List<User> Accounts { get; } = [];
        public MatchService Service { get; }

        public Stage() {
            Unit.SetupGet(unit => unit.Matches).Returns(Matches.Object);
            Unit.SetupGet(unit => unit.Users).Returns(Users.Object);
            Unit.Setup(unit => unit.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            Matches.Setup(repo => repo.Add(It.IsAny<MatchRow>())).Callback<MatchRow>(Rows.Add);
            Matches.Setup(repo => repo.Remove(It.IsAny<MatchRow>())).Callback<MatchRow>(row => Rows.Remove(row));
            Matches.Setup(repo => repo.ByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Rows.FirstOrDefault(row => row.Id == id));
            Matches.Setup(repo => repo.RecentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int take, CancellationToken _) =>
                    Rows.OrderByDescending(row => row.FinishedAt).Take(take).ToList());
            Matches.Setup(repo => repo.ForUserAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid me, int take, CancellationToken _) => Rows
                    .Where(row => row.DefenderId == me || row.AttackerId == me)
                    .OrderByDescending(row => row.FinishedAt).Take(take).ToList());
            Matches.Setup(repo => repo.StatsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MatchStats(1, [new SideWins("Defender", 1)], 5, 7, 1,
                    [new WaveStat(1, 3, 5, 4)]));
            Matches.Setup(repo => repo.LeaderboardAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int take, CancellationToken _) => Accounts
                    .Select(user => new LeaderRow(user.Id, user.Nickname, 3, 2)).Take(take).ToList());

            Users.Setup(repo => repo.ByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Accounts.Where(user => ids.Contains(user.Id)).ToList());

            Service = new MatchService(Unit.Object);
        }

        public Guid Account(string nickname) {
            var user = new User { Id = Guid.NewGuid(), Nickname = nickname };
            Accounts.Add(user);
            return user.Id;
        }

        public MatchRow Finished(Guid? defender, Guid? attacker, string winner, int waves, int hearts) =>
            new() {
                Id = Guid.NewGuid(), RoomId = Rooms.NewId(), DefenderId = defender, AttackerId = attacker,
                Winner = winner, WavesPlayed = waves, HeartsLeft = hearts, Survivor = hearts > 0,
                StartedAt = DateTime.UtcNow.AddMinutes(-3), FinishedAt = DateTime.UtcNow
            };
    }
}
