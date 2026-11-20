using Microsoft.AspNetCore.SignalR;

// The server resolves the wave even when neither player has an open tab.
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
                bool closed;
                Duel.ResultWrite? finished;
                lock (game) {
                    closed = game.CloseIfExpired();
                    finished = closed ? game.TakeResult() : null;
                }
                if (!closed) continue;
                if (finished is not null) await Storage.SaveAsync(scopes, log, id, finished);
                await bell.Clients.Group(Bell.RoomGroup(id)).SendAsync("changed", stopping);
            }
        }
    }
}
