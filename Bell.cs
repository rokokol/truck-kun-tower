using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

// Who has a live websocket right now. Friends see the dot change, nothing else depends on it.
public sealed class Presence {
    readonly ConcurrentDictionary<Guid, int> connections = new();

    // True when the user just came online, so the caller knows to announce it once
    public bool Arrive(Guid userId) => connections.AddOrUpdate(userId, 1, (_, count) => count + 1) == 1;
    public bool Leave(Guid userId) {
        if (!connections.TryGetValue(userId, out var count)) return false;
        if (count > 1) {
            connections[userId] = count - 1;
            return false;
        }
        return connections.TryRemove(userId, out _);
    }
    public bool IsOnline(Guid userId) => connections.ContainsKey(userId);
}

// The one live channel of the site: room state, invitations, friend requests and presence.
// A guest connects without a token and proves a seat with SeatOf, so the link-only game stays live too.
public class Bell(Presence presence, Rooms rooms, IHubContext<Bell> hub, IUnitOfWork unit, ILogger<Bell> log) : Hub {
    public static string RoomGroup(string id) => $"room:{id}";
    public static string UserGroup(Guid id) => $"user:{id}";

    public override async Task OnConnectedAsync() {
        if (Context.User?.UserId() is Guid id) {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(id));
            if (presence.Arrive(id)) await TellFriends(id, online: true);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? error) {
        if (Context.User?.UserId() is Guid id && presence.Leave(id)) {
            try {
                await TellFriends(id, online: false);
            } catch (Exception failure) {
                log.LogWarning(failure, "Presence of {User} was not announced", id);
            }
        }
        await base.OnDisconnectedAsync(error);
    }

    // Watching a board is allowed to its two players only; the guest half is the seat token
    public async Task JoinRoom(string roomId, string? seatToken) {
        var game = rooms.Get(roomId) ?? throw new HubException("Комната не найдена");
        var who = new PlayerRef(Context.User?.UserId(), seatToken);
        if (game.SeatOf(who) < 0) throw new HubException("Это место не ваше");
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
    }

    async Task TellFriends(Guid id, bool online) {
        var rows = await unit.Friendships.ForAsync(id, FriendshipStatus.Accepted);
        foreach (var row in rows) {
            var other = row.RequesterId == id ? row.AddresseeId : row.RequesterId;
            await hub.Clients.Group(UserGroup(other)).SendAsync("Presence", new { userId = id, online });
        }
    }
}
