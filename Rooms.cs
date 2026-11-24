using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

// The only place knows which live boards exist; Room rows in PostgreSQL let a restart mark old boards abandoned
public sealed class Rooms : IDisposable {
    internal const int SoloLimit = 128;
    readonly ConcurrentDictionary<string, Duel> games = new();
    readonly MemoryCache closed = new(new MemoryCacheOptions { SizeLimit = 1024 });
    internal SemaphoreSlim Membership { get; } = new(1, 1);

    // A finished or removed board frees its place at once
    internal int SoloCount {
        get {
            int count = 0;
            foreach (var (_, game) in games) {
                lock (game) if (game.Solo && !game.Done) count++;
            }
            return count;
        }
    }

    // CreateAsync holds rooms.Membership, so this plain check is race-free here
    internal void RequireSoloCapacity() {
        if (SoloCount >= SoloLimit)
            throw AppError.Conflict("Слишком много тренировочных дуэлей, попробуйте позже", "solo_busy");
    }

    internal void RequireAvailable(PlayerRef player, string? except = null) {
        if (player is { UserId: null, SeatToken: null }) return;
        foreach (var (id, game) in games) {
            if (id == except) continue;
            lock (game) {
                if (!game.Done && game.SeatOf(player) >= 0)
                    throw AppError.Conflict("Сначала выйди из текущей дуэли", "duel_busy");
            }
        }
    }

    public Duel? Get(string id) => games.TryGetValue(id, out var game) ? game : null;
    public bool Add(string id, Duel game) => games.TryAdd(id, game);
    public bool Remove(string id, out Duel? game) => games.TryRemove(id, out game);
    sealed record Closure(RoomStatus Status, Duel.Seat? Defender, Duel.Seat? Attacker);

    internal void Close(string id, Duel game, RoomStatus reason) {
        // A state request can beat the hub notification, so Close keeps seats instead of the board
        if (!game.Done) closed.Set(id, new Closure(reason, game.SeatAt(Duel.Defender), game.SeatAt(Duel.Attacker)),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        Remove(id, out _);
    }

    internal RoomStatus? ClosureOf(string id, PlayerRef who) =>
        closed.TryGetValue<Closure>(id, out var closure) && closure is not null
        && (closure.Defender?.Matches(who) == true || closure.Attacker?.Matches(who) == true)
            ? closure.Status : null;

    public void Dispose() {
        closed.Dispose();
        Membership.Dispose();
    }

    public IEnumerable<KeyValuePair<string, Duel>> All => games;
    public static string NewId() => Guid.NewGuid().ToString("N");
}
