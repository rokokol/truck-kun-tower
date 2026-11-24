using Microsoft.AspNetCore.SignalR;

// The wave clock also retires public rooms that have no active pair.
internal sealed class WaveClock(
    Rooms rooms,
    IHubContext<Bell> bell,
    IServiceScopeFactory scopes,
    ILogger<WaveClock> log) : BackgroundService {

    protected override async Task ExecuteAsync(CancellationToken stopping) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stopping.IsCancellationRequested) {
            try {
                await timer.WaitForNextTickAsync(stopping);
            } catch (OperationCanceledException) {
                return;
            }
            foreach (var (id, game) in rooms.All) {
                if (await TryRetireAsync(id, game, stopping)) continue;
                bool closed;
                Duel.ResultWrite? finished;
                lock (game) {
                    closed = rooms.Get(id) == game && game.CloseIfExpired();
                    finished = closed ? game.TakeResult() : null;
                }
                if (!closed) continue;
                if (finished is not null) await Storage.SaveAsync(scopes, log, id, finished);
                await bell.Clients.Group(Bell.RoomGroup(id)).SendAsync("changed", stopping);
            }
        }
    }

    async Task<bool> TryRetireAsync(string id, Duel game, CancellationToken token) {
        lock (game) if (!game.ShouldRetire(DateTime.UtcNow)) return false;
        bool finished, solo;
        await rooms.Membership.WaitAsync(token);
        try {
            lock (game) {
                if (rooms.Get(id) != game || !game.ShouldRetire(DateTime.UtcNow)) return false;
                finished = game.Done;
                solo = game.Solo;
                rooms.Close(id, game, RoomStatus.Abandoned);
            }
        } finally {
            rooms.Membership.Release();
        }
        if (!finished) {
            // A practice board has no row, so there is nothing to mark abandoned
            if (!solo) await Storage.AbandonAsync(scopes, log, id);
            await bell.Clients.Group(Bell.RoomGroup(id)).SendAsync("RoomAbandoned", id, token);
        }
        return true;
    }
}
