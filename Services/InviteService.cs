using Microsoft.AspNetCore.SignalR;

// An invite points at a live board, not at a seat: accepting only settles the invitation,
// and /api/duels/{id}/join is what actually takes the chair. So a dead room is not a join failure here.
public sealed class InviteService(IUnitOfWork unit, Rooms rooms, IHubContext<Bell> hub) {

    public async Task<List<InviteDto>> InboxAsync(Guid me) {
        var rows = await unit.Invites.IncomingAsync(me);
        return rows.Select(row => new InviteDto(row.Id,
                new UserBrief(row.FromId, row.From?.Nickname ?? ""), row.RoomId, row.CreatedAt))
            .ToList();
    }

    public async Task<InviteDto> SendAsync(Guid me, Guid? friendId, string? roomId) {
        if (friendId is not { } to) throw AppError.BadRequest("Выбери друга");
        var room = roomId?.Trim();
        if (string.IsNullOrEmpty(room)) throw AppError.BadRequest("Выбери комнату");
        var game = rooms.Get(room);
        if (game is null) throw AppError.NotFound("Комната не найдена");

        var friend = await unit.Users.ByIdAsync(to);
        if (friend is null) throw AppError.NotFound("Пользователь не найден");
        var friendship = await unit.Friendships.BetweenAsync(me, to);
        if (friendship is null || friendship.Status != FriendshipStatus.Accepted) {
            throw AppError.Forbidden("Приглашать можно только друзей");
        }
        if (game.SeatOf(new PlayerRef(to, null)) >= 0) throw AppError.Conflict("Он уже в этой комнате");
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

    public async Task<InviteAccepted> AcceptAsync(Guid me, Guid id) {
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

        invite.Status = InviteStatus.Accepted;
        invite.ResolvedAt = DateTime.UtcNow;
        await unit.SaveAsync();
        await Resolved(invite, InviteStatus.Accepted, invite.FromId);
        return new InviteAccepted(invite.RoomId);
    }

    public async Task RefuseAsync(Guid me, Guid id) {
        var invite = await unit.Invites.ByIdAsync(id);
        if (invite is null) throw AppError.NotFound("Приглашение не найдено");
        var addressee = invite.ToId == me;
        if (!addressee && invite.FromId != me) throw AppError.Forbidden("Приглашение не вам");
        if (invite.Status != InviteStatus.Pending) throw AppError.Conflict("Приглашение уже закрыто");

        var status = addressee ? InviteStatus.Declined : InviteStatus.Cancelled;
        invite.Status = status;
        invite.ResolvedAt = DateTime.UtcNow;
        await unit.SaveAsync();
        await Resolved(invite, status, addressee ? invite.FromId : invite.ToId);
    }

    // The status travels as a word, because the hub has no enum converter of its own
    Task Resolved(DuelInvite invite, InviteStatus status, Guid to) =>
        Send(to, "InviteResolved", new { id = invite.Id, status = status.ToString(), roomId = invite.RoomId });

    Task Send(Guid to, string method, object payload) =>
        hub.Clients.Group(Bell.UserGroup(to)).SendAsync(method, payload);
}
