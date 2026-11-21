using System.Collections.Concurrent;

// The only place that knows which live boards exist. A board is memory; Room rows in PostgreSQL
// say who was in it, so a restart can mark them abandoned instead of pretending they are playable.
public sealed class Rooms {
    readonly ConcurrentDictionary<string, Duel> games = new();
    internal SemaphoreSlim Membership { get; } = new(1, 1);

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
    public IEnumerable<KeyValuePair<string, Duel>> All => games;
    public static string NewId() => Guid.NewGuid().ToString("N");
}
