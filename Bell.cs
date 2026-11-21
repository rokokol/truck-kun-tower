using Microsoft.AspNetCore.SignalR;

// One account can have multiple live SignalR connections.
public sealed class Presence {
    readonly Dictionary<Guid, int> connections = new();
    readonly object gate = new();

    public bool Arrive(Guid userId) {
        lock (gate) return (connections[userId] = connections.GetValueOrDefault(userId) + 1) == 1;
    }

    public bool Leave(Guid userId) {
        lock (gate) {
            if (!connections.TryGetValue(userId, out var count)) return false;
            if (count == 1) return connections.Remove(userId);
            connections[userId] = count - 1;
            return false;
        }
    }

    public Guid[] Snapshot() {
        lock (gate) return connections.Keys.ToArray();
    }

    public bool IsOnline(Guid userId) {
        lock (gate) return connections.ContainsKey(userId);
    }
}

// The one live channel of the site: room state, invitations, friend requests and presence.
// A guest connects without a token and proves a seat with SeatOf, so the link-only game stays live too.
public class Bell(Presence presence, Rooms rooms, IHubContext<Bell> hub, IUnitOfWork unit, ILogger<Bell> log) : Hub {
    static readonly object BoardKey = new();
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
        Detach();
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
        if (Context.Items.TryGetValue(BoardKey, out var held) && held is ValueTuple<string, Duel> current &&
            current.Item1 != roomId) await LeaveRoom(current.Item1);
        lock (game) {
            if (rooms.Get(roomId) != game || !game.Watch(who, Context.ConnectionId))
                throw new HubException("Это место не ваше");
            Context.Items[BoardKey] = (roomId, game);
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
    }

    public async Task LeaveRoom(string roomId) {
        if (!Context.Items.TryGetValue(BoardKey, out var value) ||
            value is not ValueTuple<string, Duel> held || held.Item1 != roomId) return;
        Detach();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
    }

    void Detach() {
        if (!Context.Items.Remove(BoardKey, out var value) || value is not ValueTuple<string, Duel> held) return;
        lock (held.Item2) held.Item2.Unwatch(Context.ConnectionId, DateTime.UtcNow);
    }

    async Task TellFriends(Guid id, bool online) {
        var rows = await unit.Friendships.ForAsync(id, FriendshipStatus.Accepted);
        foreach (var row in rows) {
            var other = row.RequesterId == id ? row.AddresseeId : row.RequesterId;
            await hub.Clients.Group(UserGroup(other)).SendAsync("Presence", new { userId = id, online });
        }
    }
}
