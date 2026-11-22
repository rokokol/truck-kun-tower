using Microsoft.AspNetCore.SignalR;

public sealed class InviteService(IUnitOfWork unit, Rooms rooms, IHubContext<Bell> hub, Presence presence, RoomService roomService) {

    public async Task<List<InviteDto>> InboxAsync(Guid me) {
        var rows = await unit.Invites.IncomingAsync(me);
        return rows.Select(row => new InviteDto(row.Id,
                new UserBrief(row.FromId, row.From?.Nickname ?? ""), row.RoomId, row.CreatedAt))
            .ToList();
    }

    public Task<InviteDto> SendAsync(Guid me, Guid? friendId, string? roomId) =>
        WithMembership(() => SendCoreAsync(me, friendId, roomId));

    async Task<InviteDto> SendCoreAsync(Guid me, Guid? friendId, string? roomId) {
        if (friendId is not { } to || to == me) throw AppError.BadRequest("Выбери другого игрока");
        var room = roomId?.Trim();
        if (string.IsNullOrEmpty(room)) throw AppError.BadRequest("Выбери комнату");
        var game = rooms.Get(room);
        if (game is null) throw AppError.NotFound("Комната не найдена");

        lock (game) {
            if (game.SeatOf(new PlayerRef(me, null)) < 0) throw AppError.Forbidden("Эта комната не ваша");
            if (game.Done || game.Joined) throw AppError.Conflict("В дуэли нет свободного места");
        }
        var friend = await unit.Users.ByIdAsync(to);
        if (friend is null) throw AppError.NotFound("Пользователь не найден");
        var friendship = await unit.Friendships.BetweenAsync(me, to);
        if (friendship?.Status != FriendshipStatus.Accepted && !presence.IsOnline(to)) {
            throw AppError.Forbidden("Можно позвать друга или игрока, который сейчас в сети");
        }
        var first = (await unit.Invites.IncomingAsync(me))
            .Where(invite => invite.FromId == to && invite.RoomId != room)
            .OrderBy(invite => invite.CreatedAt).ThenBy(invite => invite.Id)
            .FirstOrDefault(invite => HostsWaitingRoom(invite.RoomId, to));
        if (first is not null && presence.IsOnline(to)) {
            var player = new PlayerRef(me, null);
            first.Status = InviteStatus.Accepted;
            first.ResolvedAt = DateTime.UtcNow;
            await roomService.CancelCoreAsync(player, room);
            var session = await roomService.JoinCoreAsync(player, first.RoomId, null, default);
            await Resolved(first, InviteStatus.Accepted, to);
            return new InviteDto(first.Id, new UserBrief(to, friend.Nickname), first.RoomId, first.CreatedAt, session);
        }

        if (await unit.Invites.PendingExistsAsync(me, to, room)) throw AppError.Conflict("Приглашение уже отправлено");

        var invite = new DuelInvite {
            Id = Guid.NewGuid(),
            FromId = me,
            ToId = to,
            RoomId = room,
            Status = InviteStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        unit.Invites.Add(invite);
        await unit.SaveAsync();

        var mine = await unit.Users.ByIdAsync(me);
        var dto = new InviteDto(invite.Id, new UserBrief(me, mine?.Nickname ?? ""), room, invite.CreatedAt);
        await Send(to, "DuelInvite", new { invite = dto });
        return dto;
    }

    bool HostsWaitingRoom(string id, Guid userId) {
        if (rooms.Get(id) is not { } game) return false;
        lock (game) return !game.Done && !game.Joined && game.SeatOf(new PlayerRef(userId, null)) >= 0;
    }

    public Task<InviteAccepted> AcceptAsync(Guid me, Guid id) => WithMembership(() => AcceptCoreAsync(me, id));

    async Task<InviteAccepted> AcceptCoreAsync(Guid me, Guid id) {
        var invite = await unit.Invites.ByIdAsync(id);
        if (invite is null) throw AppError.NotFound("Приглашение не найдено");
        if (invite.ToId != me) throw AppError.Forbidden("Приглашение не вам");
        if (invite.Status != InviteStatus.Pending) throw AppError.Conflict("Приглашение уже закрыто");

        // A board lives in memory: the invite outlived it, so nobody can join through this row anymore
        if (rooms.Get(invite.RoomId) is null) {
            invite.Status = InviteStatus.Expired;
            invite.ResolvedAt = DateTime.UtcNow;
            await unit.SaveAsync();
            throw AppError.Conflict("Дуэль уже закончилась");
        }

        if (!presence.IsOnline(invite.FromId)) {
            throw AppError.Conflict("Отправитель приглашения не в сети. Попробуй, когда он вернётся", "invite_sender_offline");
        }

        rooms.RequireAvailable(new PlayerRef(me, null), invite.RoomId);
        invite.Status = InviteStatus.Accepted;
        invite.ResolvedAt = DateTime.UtcNow;
        await unit.SaveAsync();
        await Resolved(invite, InviteStatus.Accepted, invite.FromId);
        return new InviteAccepted(invite.RoomId);
    }

    public Task RefuseAsync(Guid me, Guid id) => WithMembership(() => RefuseCoreAsync(me, id));

    async Task<bool> RefuseCoreAsync(Guid me, Guid id) {
        var invite = await unit.Invites.ByIdAsync(id);
        if (invite is null) throw AppError.NotFound("Приглашение не найдено");
        var addressee = invite.ToId == me;
        if (!addressee && invite.FromId != me) throw AppError.Forbidden("Приглашение не вам");
        if (invite.Status != InviteStatus.Pending) throw AppError.Conflict("Приглашение уже закрыто");

        var status = addressee ? InviteStatus.Declined : InviteStatus.Cancelled;
        invite.Status = status;
        var now = DateTime.UtcNow;
        invite.ResolvedAt = now;
        bool roomClosed = false;
        List<DuelInvite> others = [];
        if (addressee && rooms.Get(invite.RoomId) is { } game) {
            lock (game) roomClosed = !game.Joined && game.SeatOf(new PlayerRef(invite.FromId, null)) >= 0;
            if (roomClosed) {
                var row = await unit.Rooms.ByIdAsync(invite.RoomId);
                if (row is not null) {
                    row.Status = RoomStatus.Cancelled;
                    row.FinishedAt = now;
                }
                others = await CancelPendingAsync(unit, invite.RoomId, [], default, invite.Id);
            }
        }
        await unit.SaveAsync();
        if (roomClosed) rooms.Remove(invite.RoomId, out _);
        await Resolved(invite, status, addressee ? invite.FromId : invite.ToId, roomClosed);
        await NotifyCancelledAsync(hub, others);
        return true;
    }

    // The status travels as a word, because the hub has no enum converter of its own
    Task Resolved(DuelInvite invite, InviteStatus status, Guid to, bool roomClosed = false) =>
        Send(to, "InviteResolved", new { id = invite.Id, status = status.ToString(), roomId = invite.RoomId, roomClosed });

    internal static async Task<List<DuelInvite>> CancelPendingAsync(IUnitOfWork unit, string roomId, Guid[] players,
        CancellationToken token, Guid? except = null) {
        var pending = await unit.Invites.PendingForAsync(roomId, players, token);
        var closed = pending.Where(invite => invite.Id != except && invite.Status == InviteStatus.Pending).ToList();
        var now = DateTime.UtcNow;
        foreach (var invite in closed) {
            invite.Status = InviteStatus.Cancelled;
            invite.ResolvedAt = now;
        }
        return closed;
    }

    internal static async Task NotifyCancelledAsync(IHubContext<Bell> hub, List<DuelInvite> closed) {
        foreach (var invite in closed) {
            var payload = new { id = invite.Id, status = nameof(InviteStatus.Cancelled), roomId = invite.RoomId };
            await hub.Clients.Group(Bell.UserGroup(invite.ToId)).SendAsync("InviteResolved", payload);
        }
    }

    async Task<T> WithMembership<T>(Func<Task<T>> action) {
        await rooms.Membership.WaitAsync();
        try { return await action(); }
        finally { rooms.Membership.Release(); }
    }

    Task Send(Guid to, string method, object payload) =>
        hub.Clients.Group(Bell.UserGroup(to)).SendAsync(method, payload);
}
