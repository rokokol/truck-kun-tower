using Microsoft.AspNetCore.SignalR;

// A friendship is one row per pair, and the row points the way the first request went.
// Both sides read it as "the other one", so the direction only matters while the request is pending.
public sealed class FriendService(IUnitOfWork unit, Presence presence, IHubContext<Bell> hub) {

    public async Task<List<FriendDto>> ListAsync(Guid me) {
        var rows = await unit.Friendships.ForAsync(me, FriendshipStatus.Accepted);
        return rows.Select(row => new {
                Other = row.RequesterId == me ? row.AddresseeId : row.RequesterId,
                User = row.RequesterId == me ? row.Addressee : row.Requester,
                // A pair that was accepted through the counter-request path still has an AcceptedAt
                Since = row.AcceptedAt ?? row.CreatedAt
            })
            .Select(row => new FriendDto(row.Other, row.User?.Nickname ?? "", presence.IsOnline(row.Other), row.Since))
            .OrderBy(friend => friend.Nickname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RequestListDto> RequestsAsync(Guid me) {
        var rows = await unit.Friendships.ForAsync(me, FriendshipStatus.Pending);
        var incoming = rows.Where(row => row.AddresseeId == me)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new IncomingRequestDto(row.Id,
                new UserBrief(row.RequesterId, row.Requester?.Nickname ?? ""), row.CreatedAt))
            .ToList();
        var outgoing = rows.Where(row => row.RequesterId == me)
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => new OutgoingRequestDto(row.Id,
                new UserBrief(row.AddresseeId, row.Addressee?.Nickname ?? ""), row.CreatedAt))
            .ToList();
        return new RequestListDto(incoming, outgoing);
    }

    // Asking someone who already asked me is an agreement, so that pair becomes friends at once.
    public async Task<IncomingRequestDto> AskAsync(Guid me, string? nickname) {
        if (string.IsNullOrWhiteSpace(nickname)) throw AppError.BadRequest("Введи ник");

        var target = await unit.Users.ByNicknameAsync(nickname);
        if (target is null) throw AppError.NotFound("Пользователь не найден");
        if (target.Id == me) throw AppError.BadRequest("Нельзя добавить себя");

        var row = await unit.Friendships.BetweenAsync(me, target.Id);
        if (row is null) {
            row = new Friendship {
                Id = Guid.NewGuid(),
                RequesterId = me,
                AddresseeId = target.Id,
                Status = FriendshipStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            unit.Friendships.Add(row);
            await unit.SaveAsync();
            var sent = new IncomingRequestDto(row.Id, new UserBrief(target.Id, target.Nickname), row.CreatedAt);
            await Send(target.Id, "FriendRequest", new { request = sent });
            return sent;
        }

        if (row.Status == FriendshipStatus.Accepted) throw AppError.Conflict("Вы уже друзья");
        if (row.RequesterId == me) throw AppError.Conflict("Заявка уже отправлена");

        // The request hangs from him to me: my answer is the agreement itself
        row.Status = FriendshipStatus.Accepted;
        row.AcceptedAt = DateTime.UtcNow;
        await unit.SaveAsync();
        var mine = await BriefAsync(me, row.Addressee);
        var his = await BriefAsync(row.RequesterId, row.Requester);
        await Accepted(row.RequesterId, mine);
        await Accepted(me, his);
        return new IncomingRequestDto(row.Id, his, row.CreatedAt);
    }

    public async Task<IncomingRequestDto> AcceptAsync(Guid me, Guid id) {
        var row = await unit.Friendships.ByIdAsync(id);
        if (row is null) throw AppError.NotFound("Заявка не найдена");
        if (row.AddresseeId != me) throw AppError.Forbidden("Заявку принимает адресат");
        if (row.Status != FriendshipStatus.Pending) throw AppError.Conflict("Заявка уже не висит");

        row.Status = FriendshipStatus.Accepted;
        row.AcceptedAt = DateTime.UtcNow;
        await unit.SaveAsync();
        var mine = await BriefAsync(me, row.Addressee);
        var his = await BriefAsync(row.RequesterId, row.Requester);
        await Accepted(row.RequesterId, mine);
        await Accepted(me, his);
        return new IncomingRequestDto(row.Id, his, row.CreatedAt);
    }

    // The addressee declines, the requester cancels; either way the row goes away
    public async Task RefuseAsync(Guid me, Guid id) {
        var row = await unit.Friendships.ByIdAsync(id);
        if (row is null) throw AppError.NotFound("Заявка не найдена");
        var addressee = row.AddresseeId == me;
        if (!addressee && row.RequesterId != me) throw AppError.Forbidden("Заявка не вам");
        if (row.Status != FriendshipStatus.Pending) throw AppError.Conflict("Заявка уже не висит");

        unit.Friendships.Remove(row);
        await unit.SaveAsync();
        await Send(addressee ? row.RequesterId : row.AddresseeId, "FriendRemoved", new { userId = me });
    }

    public async Task RemoveAsync(Guid me, Guid friendId) {
        var row = await unit.Friendships.BetweenAsync(me, friendId);
        if (row is null || row.Status != FriendshipStatus.Accepted) throw AppError.NotFound("Такой дружбы нет");

        unit.Friendships.Remove(row);
        await unit.SaveAsync();
        await Send(friendId, "FriendRemoved", new { userId = me });
        await Send(me, "FriendRemoved", new { userId = friendId });
    }

    Task Send(Guid to, string method, object payload) =>
        hub.Clients.Group(Bell.UserGroup(to)).SendAsync(method, payload);

    Task Accepted(Guid to, UserBrief friend) => Send(to, "FriendAccepted", new { friend });

    async Task<UserBrief> BriefAsync(Guid id, User? known) {
        var user = known ?? await unit.Users.ByIdAsync(id);
        return new UserBrief(id, user?.Nickname ?? "");
    }
}
