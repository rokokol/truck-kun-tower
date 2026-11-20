using Microsoft.AspNetCore.SignalR;

// Runs the thirty-second clock: a player who stops answering does not hold the wave
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
                    finished = game.TakeResult();
                }
                if (!closed) continue;
                if (finished is not null) await Storage.SaveAsync(scopes, log, id, finished);
                await bell.Clients.Group(Bell.RoomGroup(id)).SendAsync("changed", stopping);
            }
        }
    }
}
