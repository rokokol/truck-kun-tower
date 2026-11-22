using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

// Friends and invites are rules about who may ask whom, and what the other side hears about it.
// These tests drive the services against mocked repositories: no database, no HTTP, no hub.
public sealed class FriendTests {
    static readonly Guid Me = new("11111111-1111-1111-1111-111111111111");
    static readonly Guid Mate = new("22222222-2222-2222-2222-222222222222");
    static readonly Guid Stranger = new("33333333-3333-3333-3333-333333333333");
    const string Board = "board";

    // A nickname must reach the assertion unchanged instead of arriving as \uXXXX escapes
    static readonly JsonSerializerOptions PayloadJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // One place to build the mocked world, and to read back what it was told
    sealed class Harness {
        public sealed record SentEvent(string Group, string Method, object?[] Args);

        public Mock<IUnitOfWork> Unit { get; } = new();
        public Mock<IUserRepository> Users { get; } = new();
        public Mock<IFriendshipRepository> Friendships { get; } = new();
        public Mock<IInviteRepository> Invites { get; } = new();
        public Mock<IHubContext<Bell>> Hub { get; } = new();
        public Presence Presence { get; } = new();
        public Rooms Rooms { get; } = new();
        public List<SentEvent> Sent { get; } = [];

        string group = "";

        public Harness() {
            Unit.SetupGet(row => row.Users).Returns(Users.Object);
            Unit.SetupGet(row => row.Friendships).Returns(Friendships.Object);
            Unit.SetupGet(row => row.Invites).Returns(Invites.Object);
            Unit.SetupGet(row => row.Rooms).Returns(Mock.Of<IRoomRepository>());
            Invites.Setup(repo => repo.IncomingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Invites.Setup(repo => repo.PendingForAsync(It.IsAny<string>(), It.IsAny<Guid[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            Unit.Setup(row => row.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var proxy = new Mock<IClientProxy>();
            proxy.Setup(client => client.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((method, args, _) =>
                    Sent.Add(new SentEvent(group, method, args)))
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(hubClients => hubClients.Group(It.IsAny<string>()))
                .Callback<string>(name => group = name)
                .Returns(proxy.Object);
            Hub.SetupGet(context => context.Clients).Returns(clients.Object);
        }

        public FriendService Friends => new(Unit.Object, Presence, Hub.Object);
        public InviteService InviteService => new(Unit.Object, Rooms, Hub.Object, Presence,
            new RoomService(Rooms, Unit.Object, Hub.Object,
                Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RoomService>.Instance));

        public User User(Guid id, string nickname) {
            var user = new User { Id = id, Nickname = nickname, NicknameKey = Nicknames.Normalize(nickname) };
            Users.Setup(repo => repo.ByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
            return user;
        }

        public void KnownByNickname(string nickname, User? user) =>
            Users.Setup(repo => repo.ByNicknameAsync(nickname, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        // The repository looks a pair up from either side, and by id, so the mock answers the same row
        public Friendship Pair(FriendshipStatus status, Guid requester, Guid addressee, Guid? id = null) {
            var row = new Friendship {
                Id = id ?? Guid.NewGuid(),
                RequesterId = requester,
                AddresseeId = addressee,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                AcceptedAt = status == FriendshipStatus.Accepted ? DateTime.UtcNow.AddMinutes(-1) : null
            };
            Friendships.Setup(repo => repo.BetweenAsync(requester, addressee, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);
            Friendships.Setup(repo => repo.BetweenAsync(addressee, requester, It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);
            Friendships.Setup(repo => repo.ByIdAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
            return row;
        }

        public DuelInvite Invite(InviteStatus status, Guid from, Guid to, string room) {
            var row = new DuelInvite {
                Id = Guid.NewGuid(),
                FromId = from,
                ToId = to,
                RoomId = room,
                Status = status,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            };
            Invites.Setup(repo => repo.ByIdAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
            return row;
        }

        // A live board costs nothing here: no clock runs until somebody joins
        public Duel Live() {
            var game = new Duel();
            Assert.True(game.TryTake(new PlayerRef(Me, null), Sides.Defender, out _));
            Rooms.Add(Board, game);
            return game;
        }

        // A payload is anonymous, so the test reads it back as JSON: no alias type to keep in step
        public string Payload(int index) {
            var payload = Sent[index].Args[0];
            return JsonSerializer.Serialize(payload, payload?.GetType() ?? typeof(object), PayloadJson);
        }
    }

    [Fact]
    public async Task Asking_yourself_is_a_bad_request() {
        var h = new Harness();
        var me = h.User(Me, "я");
        h.KnownByNickname("я", me);

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.AskAsync(Me, "я"));

        Assert.Equal(400, error.Status);
        h.Friendships.Verify(repo => repo.Add(It.IsAny<Friendship>()), Times.Never);
    }

    [Fact]
    public async Task Asking_a_nickname_nobody_holds_is_not_found() {
        var h = new Harness();
        h.KnownByNickname("призрак", null);

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.AskAsync(Me, "призрак"));

        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Asking_again_after_the_pair_is_friends_conflicts() {
        var h = new Harness();
        var mate = h.User(Mate, "Маша");
        h.KnownByNickname("Маша", mate);
        h.Pair(FriendshipStatus.Accepted, Mate, Me);

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.AskAsync(Me, "Маша"));

        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task A_pending_request_is_stored_and_announced_to_the_addressee() {
        var h = new Harness();
        h.User(Me, "Петя");
        var mate = h.User(Mate, "Маша");
        h.KnownByNickname("Маша", mate);
        Friendship? added = null;
        h.Friendships.Setup(repo => repo.Add(It.IsAny<Friendship>()))
            .Callback<Friendship>(row => added = row);

        var request = await h.Friends.AskAsync(Me, "Маша");

        Assert.NotNull(added);
        Assert.Equal(Me, added!.RequesterId);
        Assert.Equal(Mate, added.AddresseeId);
        Assert.Equal(FriendshipStatus.Pending, added.Status);
        Assert.Equal(added.Id, request.Id);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(Bell.UserGroup(Mate), sent.Group);
        Assert.Equal("FriendRequest", sent.Method);
        using var message = JsonDocument.Parse(h.Payload(0));
        var from = message.RootElement.GetProperty("request").GetProperty("From");
        Assert.Equal(Me, from.GetProperty("Id").GetGuid());
        Assert.Equal("Петя", from.GetProperty("Nickname").GetString());
    }

    [Fact]
    public async Task Asking_someone_who_already_asked_me_makes_the_pair_friends() {
        var h = new Harness();
        var mate = h.User(Mate, "Маша");
        h.User(Me, "Петя");
        h.KnownByNickname("Маша", mate);
        var row = h.Pair(FriendshipStatus.Pending, Mate, Me);

        var answer = await h.Friends.AskAsync(Me, "Маша");

        Assert.Equal(FriendshipStatus.Accepted, row.Status);
        Assert.NotNull(row.AcceptedAt);
        Assert.Equal(Mate, answer.From.Id);
        Assert.Equal(["FriendAccepted", "FriendAccepted"], h.Sent.Select(sent => sent.Method));
        Assert.Equal([Bell.UserGroup(Mate), Bell.UserGroup(Me)], h.Sent.Select(sent => sent.Group));
        Assert.Contains("Петя", h.Payload(0));
        Assert.Contains("Маша", h.Payload(1));
    }

    [Fact]
    public async Task Only_the_addressee_may_accept_a_request() {
        var h = new Harness();
        var row = h.Pair(FriendshipStatus.Pending, Mate, Stranger);

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.AcceptAsync(Me, row.Id));

        Assert.Equal(403, error.Status);
        Assert.Equal(FriendshipStatus.Pending, row.Status);
        h.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accepting_an_unknown_request_is_not_found() {
        var h = new Harness();

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.AcceptAsync(Me, Guid.NewGuid()));

        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Accepting_a_request_tells_both_sides_who_the_friend_is() {
        var h = new Harness();
        var row = h.Pair(FriendshipStatus.Pending, Mate, Me);
        row.Requester = h.User(Mate, "Маша");
        row.Addressee = h.User(Me, "Петя");

        var accepted = await h.Friends.AcceptAsync(Me, row.Id);

        Assert.Equal(FriendshipStatus.Accepted, row.Status);
        Assert.Equal(Mate, accepted.From.Id);
        Assert.Equal(2, h.Sent.Count);
        Assert.All(h.Sent, sent => Assert.Equal("FriendAccepted", sent.Method));
        Assert.Contains("Петя", h.Payload(0));
        Assert.Contains("Маша", h.Payload(1));
    }

    [Fact]
    public async Task Removing_someone_who_is_not_a_friend_is_not_found() {
        var h = new Harness();

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.RemoveAsync(Me, Mate));

        Assert.Equal(404, error.Status);
        h.Friendships.Verify(repo => repo.Remove(It.IsAny<Friendship>()), Times.Never);
    }

    [Fact]
    public async Task Removing_a_friend_tells_both_sides_and_drops_the_row() {
        var h = new Harness();
        var row = h.Pair(FriendshipStatus.Accepted, Me, Mate);

        await h.Friends.RemoveAsync(Me, Mate);

        h.Friendships.Verify(repo => repo.Remove(row), Times.Once);
        Assert.Equal(["FriendRemoved", "FriendRemoved"], h.Sent.Select(sent => sent.Method));
        Assert.Contains(Me.ToString(), h.Payload(0));
        Assert.Contains(Mate.ToString(), h.Payload(1));
    }

    [Fact]
    public async Task Refusing_my_own_request_cancels_it_for_the_addressee() {
        var h = new Harness();
        var row = h.Pair(FriendshipStatus.Pending, Me, Mate);

        await h.Friends.RefuseAsync(Me, row.Id);

        h.Friendships.Verify(repo => repo.Remove(row), Times.Once);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(Bell.UserGroup(Mate), sent.Group);
        Assert.Equal("FriendRemoved", sent.Method);
        Assert.Contains(Me.ToString(), h.Payload(0));
    }

    [Fact]
    public async Task Somebody_else_cannot_refuse_my_request() {
        var h = new Harness();
        var row = h.Pair(FriendshipStatus.Pending, Mate, Stranger);

        var error = await Assert.ThrowsAsync<AppError>(() => h.Friends.RefuseAsync(Me, row.Id));

        Assert.Equal(403, error.Status);
        h.Friendships.Verify(repo => repo.Remove(It.IsAny<Friendship>()), Times.Never);
    }

    [Fact]
    public async Task The_friend_list_is_sorted_by_nickname_and_knows_who_is_online() {
        var h = new Harness();
        var anna = h.User(Stranger, "Аня");
        var boris = h.User(Mate, "Борис");
        h.Friendships.Setup(repo => repo.ForAsync(Me, FriendshipStatus.Accepted, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Friendship {
                    Id = Guid.NewGuid(), RequesterId = Me, AddresseeId = boris.Id,
                    Status = FriendshipStatus.Accepted, CreatedAt = DateTime.UtcNow.AddDays(-2),
                    AcceptedAt = DateTime.UtcNow.AddDays(-1), Addressee = boris
                },
                new Friendship {
                    Id = Guid.NewGuid(), RequesterId = anna.Id, AddresseeId = Me,
                    Status = FriendshipStatus.Accepted, CreatedAt = DateTime.UtcNow.AddDays(-3),
                    AcceptedAt = DateTime.UtcNow.AddDays(-2), Requester = anna
                }
            ]);
        h.Presence.Arrive(anna.Id);

        var friends = await h.Friends.ListAsync(Me);

        Assert.Equal(["Аня", "Борис"], friends.Select(friend => friend.Nickname));
        Assert.True(friends[0].Online);
        Assert.False(friends[1].Online);
        Assert.Equal(DateTime.UtcNow.AddDays(-2).Date, friends[0].FriendsSince.Date);
    }

    [Fact]
    public async Task Requests_split_by_direction_and_newest_first() {
        var h = new Harness();
        var old = new Friendship {
            Id = Guid.NewGuid(), RequesterId = Stranger, AddresseeId = Me, Status = FriendshipStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddHours(-2), Requester = new User { Id = Stranger, Nickname = "Аня" }
        };
        var fresh = new Friendship {
            Id = Guid.NewGuid(), RequesterId = Me, AddresseeId = Mate, Status = FriendshipStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1), Addressee = new User { Id = Mate, Nickname = "Борис" }
        };
        h.Friendships.Setup(repo => repo.ForAsync(Me, FriendshipStatus.Pending, It.IsAny<CancellationToken>()))
            .ReturnsAsync([old, fresh]);

        var requests = await h.Friends.RequestsAsync(Me);

        Assert.Equal(old.Id, Assert.Single(requests.Incoming).Id);
        Assert.Equal(fresh.Id, Assert.Single(requests.Outgoing).Id);
        Assert.Equal("Аня", requests.Incoming[0].From.Nickname);
        Assert.Equal("Борис", requests.Outgoing[0].To.Nickname);
    }

    [Fact]
    public async Task Inviting_an_offline_stranger_is_forbidden() {
        var h = new Harness();
        h.Live();
        h.User(Mate, "Маша");

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.SendAsync(Me, Mate, Board));

        Assert.Equal(403, error.Status);
        h.Invites.Verify(repo => repo.Add(It.IsAny<DuelInvite>()), Times.Never);
    }

    [Fact]
    public async Task An_online_player_can_be_invited_without_a_friendship() {
        var h = new Harness();
        h.Live();
        h.User(Me, "Петя");
        h.User(Mate, "Маша");
        h.Presence.Arrive(Mate);
        var invite = await h.InviteService.SendAsync(Me, Mate, Board);
        Assert.Equal(Board, invite.RoomId);
        Assert.Equal(Bell.UserGroup(Mate), Assert.Single(h.Sent).Group);
    }

    [Fact]
    public async Task Inviting_the_same_friend_into_the_same_room_twice_conflicts() {
        var h = new Harness();
        h.Live();
        h.User(Me, "Петя");
        h.User(Mate, "Маша");
        h.Pair(FriendshipStatus.Accepted, Me, Mate);
        h.Invites.Setup(repo => repo.PendingExistsAsync(Me, Mate, Board, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.SendAsync(Me, Mate, Board));

        Assert.Equal(409, error.Status);
        h.Invites.Verify(repo => repo.Add(It.IsAny<DuelInvite>()), Times.Never);
    }

    [Fact]
    public async Task Inviting_a_friend_who_already_sits_in_the_room_conflicts() {
        var h = new Harness();
        var game = h.Live();
        Assert.True(game.TryTake(new PlayerRef(Mate, null), Sides.Attacker, out _));
        h.User(Me, "Петя");
        h.User(Mate, "Маша");
        h.Pair(FriendshipStatus.Accepted, Me, Mate);

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.SendAsync(Me, Mate, Board));

        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task An_invite_is_stored_and_announced_to_the_friend() {
        var h = new Harness();
        h.Live();
        h.User(Me, "Петя");
        h.User(Mate, "Маша");
        h.Pair(FriendshipStatus.Accepted, Me, Mate);
        DuelInvite? added = null;
        h.Invites.Setup(repo => repo.Add(It.IsAny<DuelInvite>())).Callback<DuelInvite>(row => added = row);

        var invite = await h.InviteService.SendAsync(Me, Mate, Board);

        Assert.NotNull(added);
        Assert.Equal(InviteStatus.Pending, added!.Status);
        Assert.Equal(Mate, added.ToId);
        Assert.Equal(invite.Id, added.Id);
        Assert.Equal("Петя", invite.From.Nickname);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(Bell.UserGroup(Mate), sent.Group);
        Assert.Equal("DuelInvite", sent.Method);
        using var message = JsonDocument.Parse(h.Payload(0));
        var payload = message.RootElement.GetProperty("invite");
        Assert.Equal(Board, payload.GetProperty("RoomId").GetString());
        var from = payload.GetProperty("From");
        Assert.Equal(Me, from.GetProperty("Id").GetGuid());
        Assert.Equal("Петя", from.GetProperty("Nickname").GetString());
    }

    [Fact]
    public async Task Only_the_addressee_may_accept_an_invite() {
        var h = new Harness();
        h.Live();
        var invite = h.Invite(InviteStatus.Pending, Mate, Stranger, Board);

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.AcceptAsync(Me, invite.Id));

        Assert.Equal(403, error.Status);
        Assert.Equal(InviteStatus.Pending, invite.Status);
    }

    [Fact]
    public async Task Accepting_an_invite_to_a_room_that_is_gone_conflicts_and_expires_it() {
        var h = new Harness();
        var invite = h.Invite(InviteStatus.Pending, Mate, Me, "gone");

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.AcceptAsync(Me, invite.Id));

        Assert.Equal(409, error.Status);
        Assert.Equal(InviteStatus.Expired, invite.Status);
        Assert.NotNull(invite.ResolvedAt);
        h.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Accepting_an_invite_waits_for_the_sender_to_return_online() {
        var h = new Harness();
        h.Live();
        var invite = h.Invite(InviteStatus.Pending, Mate, Me, Board);

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.AcceptAsync(Me, invite.Id));

        Assert.Equal(409, error.Status);
        Assert.Equal("invite_sender_offline", error.Code);
        Assert.Equal(InviteStatus.Pending, invite.Status);
        Assert.Null(invite.ResolvedAt);
        Assert.Empty(h.Sent);
        h.Unit.Verify(unit => unit.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);

        h.Presence.Arrive(Mate);
        var accepted = await h.InviteService.AcceptAsync(Me, invite.Id);

        Assert.Equal(Board, accepted.RoomId);
        Assert.Equal(InviteStatus.Accepted, invite.Status);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(Bell.UserGroup(Mate), sent.Group);
        Assert.Equal("InviteResolved", sent.Method);
        Assert.Contains("\"status\":\"Accepted\"", h.Payload(0));
    }

    [Fact]
    public async Task Refusing_an_invite_marks_it_declined_and_tells_the_sender() {
        var h = new Harness();
        var invite = h.Invite(InviteStatus.Pending, Mate, Me, Board);

        await h.InviteService.RefuseAsync(Me, invite.Id);

        Assert.Equal(InviteStatus.Declined, invite.Status);
        Assert.Equal("InviteResolved", Assert.Single(h.Sent).Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decline_closes_only_a_waiting_room(bool joined) {
        var h = new Harness();
        var game = h.Live();
        if (joined) Assert.True(game.TryTake(new PlayerRef(Stranger, null), Sides.Attacker, out _));
        var invite = h.Invite(InviteStatus.Pending, Me, Mate, Board);

        await h.InviteService.RefuseAsync(Mate, invite.Id);

        Assert.Equal(joined, h.Rooms.Get(Board) is not null);
        Assert.Equal(Bell.UserGroup(Me), Assert.Single(h.Sent).Group);
        Assert.Contains(joined ? "\"roomClosed\":false" : "\"roomClosed\":true", h.Payload(0));
    }

    [Fact]
    public async Task Cancelling_an_invite_marks_it_cancelled_and_tells_the_addressee() {
        var h = new Harness();
        var invite = h.Invite(InviteStatus.Pending, Me, Mate, Board);

        await h.InviteService.RefuseAsync(Me, invite.Id);

        Assert.Equal(InviteStatus.Cancelled, invite.Status);
        var sent = Assert.Single(h.Sent);
        Assert.Equal(Bell.UserGroup(Mate), sent.Group);
        Assert.Contains("\"status\":\"Cancelled\"", h.Payload(0));
    }

    [Fact]
    public async Task A_stranger_cannot_touch_somebody_elses_invite() {
        var h = new Harness();
        var invite = h.Invite(InviteStatus.Pending, Mate, Stranger, Board);

        var error = await Assert.ThrowsAsync<AppError>(() => h.InviteService.RefuseAsync(Me, invite.Id));

        Assert.Equal(403, error.Status);
        Assert.Equal(InviteStatus.Pending, invite.Status);
    }
}
