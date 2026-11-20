using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace TruckKun.Tower.Tests;

// The service is exercised against a real Rooms and a fake database: every rule here is about
// who sits where, not about PostgreSQL, and the rows are checked as objects the mocks collected.
public sealed class RoomTests {

    [Fact]
    public async Task A_taken_side_answers_with_a_conflict() {
        var stage = new Stage();
        var host = stage.Account("Хозяин");
        var session = await stage.Service.CreateAsync(new PlayerRef(host, null), Duel.Defender);
        var guest = PlayerRef.Nobody;

        var error = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.JoinAsync(guest, session.Id, Duel.Defender));

        Assert.Equal(409, error.Status);
        Assert.Equal(1, stage.Rooms.Get(session.Id)!.Occupied);
    }

    [Fact]
    public async Task A_reload_returns_the_same_seat_instead_of_a_conflict() {
        var stage = new Stage();
        var host = stage.Account("Хозяин");
        var me = new PlayerRef(host, null);
        var session = await stage.Service.CreateAsync(me, Duel.Defender);

        var again = await stage.Service.JoinAsync(me, session.Id, Duel.Attacker);

        Assert.Equal("defender", again.Side);
        Assert.Equal(session.SeatToken, again.SeatToken);
        Assert.Equal(1, stage.Rooms.Get(session.Id)!.Occupied);
    }

    [Fact]
    public async Task A_guest_keeps_the_link_and_leaves_no_row() {
        var stage = new Stage();
        var session = await stage.Service.CreateAsync(PlayerRef.Nobody, Duel.Defender);

        var again = await stage.Service.JoinAsync(new PlayerRef(null, session.SeatToken), session.Id, Duel.Defender);

        Assert.Equal("defender", again.Side);
        Assert.Equal(session.SeatToken, again.SeatToken);
        Assert.Empty(stage.Stored);
        stage.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_player_alone_moves_to_the_other_side() {
        var stage = new Stage();
        var host = stage.Account("Один");
        var me = new PlayerRef(host, null);
        var session = await stage.Service.CreateAsync(me, Duel.Defender);

        await stage.Service.AskSwapAsync(me, session.Id);

        var game = stage.Rooms.Get(session.Id)!;
        Assert.Equal(Duel.Attacker, game.SeatOf(me));
        Assert.Null(game.SeatAt(Duel.Defender));
        var row = Assert.Single(stage.Stored);
        Assert.Null(row.DefenderId);
        Assert.Equal(host, row.AttackerId);
    }

    [Fact]
    public async Task Two_players_swap_only_when_the_other_agrees() {
        var stage = new Stage();
        var first = stage.Account("Первый");
        var second = stage.Account("Второй");
        var defender = new PlayerRef(first, null);
        var attacker = new PlayerRef(second, null);
        var session = await stage.Service.CreateAsync(defender, Duel.Defender);
        await stage.Service.JoinAsync(attacker, session.Id, Duel.Attacker);
        var game = stage.Rooms.Get(session.Id)!;

        await stage.Service.AskSwapAsync(defender, session.Id);
        Assert.Equal(Duel.Defender, game.SwapRequestedBy);

        await stage.Service.AnswerSwapAsync(attacker, session.Id, accept: false);
        Assert.Null(game.SwapRequestedBy);
        Assert.Equal(Duel.Defender, game.SeatOf(defender));

        await stage.Service.AskSwapAsync(defender, session.Id);
        await stage.Service.AnswerSwapAsync(attacker, session.Id, accept: true);

        Assert.Equal(Duel.Attacker, game.SeatOf(defender));
        Assert.Equal(Duel.Defender, game.SeatOf(attacker));
        var row = Assert.Single(stage.Stored);
        Assert.Equal(second, row.DefenderId);
        Assert.Equal(first, row.AttackerId);
    }

    [Fact]
    public async Task Sides_freeze_once_the_first_wave_is_played() {
        var stage = new Stage();
        var first = stage.Account("Защитник");
        var second = stage.Account("Атакующий");
        var defender = new PlayerRef(first, null);
        var session = await stage.Service.CreateAsync(defender, Duel.Defender);
        await stage.Service.JoinAsync(new PlayerRef(second, null), session.Id, Duel.Attacker);
        var game = stage.Rooms.Get(session.Id)!;

        var gate = Buy(game);
        Assert.Null(game.Apply(Duel.Defender, new Move(1, "ready", null)));
        Assert.Null(game.Apply(Duel.Attacker, new Move(1, "ready", null)));

        var error = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.AskSwapAsync(defender, session.Id));

        Assert.Equal(409, error.Status);
        // The wave really was played: its order is gone and the second one is open
        Assert.NotNull(game.Apply(Duel.Attacker, new Move(1, "swarm", gate)));
        Assert.Null(game.Apply(Duel.Attacker, new Move(2, "swarm", gate)));
    }

    [Fact]
    public async Task A_refused_move_keeps_the_code_the_board_chose() {
        var stage = new Stage();
        var first = stage.Account("Защитник");
        var second = stage.Account("Атакующий");
        var defender = new PlayerRef(first, null);
        var attacker = new PlayerRef(second, null);
        var session = await stage.Service.CreateAsync(defender, Duel.Defender);
        await stage.Service.JoinAsync(attacker, session.Id, Duel.Attacker);

        // A defender cannot buy slimes, and an attacker cannot lock a wave without an order
        var wrong_role = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.MoveAsync(defender, session.Id, new Move(1, "swarm", 45)));
        var empty_wave = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.MoveAsync(attacker, session.Id, new Move(1, "ready", null)));

        Assert.Equal(400, wrong_role.Status);
        Assert.Equal(409, empty_wave.Status);
        Assert.Equal("Этот ход недоступен", wrong_role.Message);
        // Nothing was spent: a refused move leaves the board where it was
        Assert.True(stage.Rooms.Get(session.Id)!.CanSwitch);
    }

    [Fact]
    public async Task Cancelling_removes_the_board_and_closes_the_row() {
        var stage = new Stage();
        var host = stage.Account("Хозяин");
        var me = new PlayerRef(host, null);
        var session = await stage.Service.CreateAsync(me, Duel.Defender);

        await stage.Service.CancelAsync(me, session.Id);

        Assert.Null(stage.Rooms.Get(session.Id));
        Assert.Equal(RoomStatus.Cancelled, Assert.Single(stage.Stored).Status);
        var error = await Assert.ThrowsAsync<AppError>(() => stage.Service.CancelAsync(me, session.Id));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task A_stranger_cannot_read_or_cancel_someone_elses_room() {
        var stage = new Stage();
        var host = stage.Account("Хозяин");
        var stranger = stage.Account("Чужой");
        var session = await stage.Service.CreateAsync(new PlayerRef(host, null), Duel.Defender);

        var read = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.StateAsync(new PlayerRef(stranger, null), session.Id));
        var cancel = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.CancelAsync(new PlayerRef(stranger, null), session.Id));

        Assert.Equal(403, read.Status);
        Assert.Equal(403, cancel.Status);
        Assert.NotNull(stage.Rooms.Get(session.Id));
    }

    [Fact]
    public async Task My_rooms_come_newest_first_with_the_rival_and_the_live_status() {
        var stage = new Stage();
        var me = stage.Account("Я");
        var rival = stage.Account("Соперник");
        var now = DateTime.UtcNow;
        stage.Stored.Add(new Room {
            Id = "live", OwnerId = me, DefenderId = me, AttackerId = rival,
            Status = RoomStatus.Waiting, CreatedAt = now
        });
        stage.Stored.Add(new Room {
            Id = "done", OwnerId = me, DefenderId = rival, AttackerId = me,
            Status = RoomStatus.Finished, CreatedAt = now.AddMinutes(-1), FinishedAt = now
        });
        var game = new Duel();
        game.TryTake(new PlayerRef(me, null), Duel.Defender, out _);
        game.TryTake(new PlayerRef(rival, null), Duel.Attacker, out _);
        stage.Rooms.Add("live", game);

        var list = await stage.Service.MineAsync(me);

        Assert.Equal(2, list.Count);
        Assert.Equal("live", list[0].Id);
        Assert.Equal("defender", list[0].MySide);
        Assert.Equal("Playing", list[0].Status);
        Assert.Equal("Соперник", list[0].Opponent!.Nickname);
        Assert.Equal("done", list[1].Id);
        Assert.Equal("attacker", list[1].MySide);
        Assert.Equal("Finished", list[1].Status);
    }

    // A wave order is a purchase from one of the four gates the maze opens on the edge
    static int Buy(Duel game) {
        foreach (var cell in new[] { 45, 105, 3, 5, 9, 11, 153, 155, 159, 161 })
            if (game.Apply(Duel.Attacker, new Move(1, "swarm", cell)) is null) return cell;
        throw new InvalidOperationException("The maze refused every gate");
    }

    sealed class Stage {
        public Rooms Rooms { get; } = new();
        public Mock<IUnitOfWork> Unit { get; } = new();
        public Mock<IRoomRepository> Rows { get; } = new();
        public Mock<IUserRepository> Users { get; } = new();
        public Mock<IClientProxy> Channel { get; } = new();
        public List<Room> Stored { get; } = [];
        public List<User> Accounts { get; } = [];
        public RoomService Service { get; }

        public Stage() {
            Unit.SetupGet(unit => unit.Rooms).Returns(Rows.Object);
            Unit.SetupGet(unit => unit.Users).Returns(Users.Object);
            Unit.Setup(unit => unit.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            Rows.Setup(repo => repo.Add(It.IsAny<Room>())).Callback<Room>(Stored.Add);
            Rows.Setup(repo => repo.ByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Stored.FirstOrDefault(row => row.Id == id));
            Rows.Setup(repo => repo.ForUserAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid me, int take, CancellationToken _) => Stored
                    .Where(row => row.OwnerId == me || row.DefenderId == me || row.AttackerId == me)
                    .OrderByDescending(row => row.CreatedAt).Take(take).ToList());

            Users.Setup(repo => repo.ByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Accounts.FirstOrDefault(user => user.Id == id));
            Users.Setup(repo => repo.ByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Accounts.Where(user => ids.Contains(user.Id)).ToList());

            var clients = new Mock<IHubClients>();
            clients.Setup(all => all.Group(It.IsAny<string>())).Returns(Channel.Object);
            var hub = new Mock<IHubContext<Bell>>();
            hub.SetupGet(context => context.Clients).Returns(clients.Object);
            Channel.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Service = new RoomService(Rooms, Unit.Object, hub.Object, Mock.Of<IServiceScopeFactory>(),
                NullLogger<RoomService>.Instance);
        }

        public Guid Account(string nickname) {
            var user = new User { Id = Guid.NewGuid(), Nickname = nickname };
            Accounts.Add(user);
            return user.Id;
        }
    }
}
