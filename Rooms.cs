using System.Collections.Concurrent;

// The only place that knows which live boards exist. A board is memory; Room rows in PostgreSQL
// say who was in it, so a restart can mark them abandoned instead of pretending they are playable.
public sealed class Rooms {
    readonly ConcurrentDictionary<string, Duel> games = new();

    public Duel? Get(string id) => games.TryGetValue(id, out var game) ? game : null;
    public bool Add(string id, Duel game) => games.TryAdd(id, game);
    public bool Remove(string id, out Duel? game) => games.TryRemove(id, out game);
    public IEnumerable<KeyValuePair<string, Duel>> All => games;
    public static string NewId() => Guid.NewGuid().ToString("N");
}
