using Microsoft.EntityFrameworkCore;

// One place that turns a finished duel into rows, so the endpoint and the wave clock share it
internal static class Storage {
    public static async Task SaveAsync(IServiceScopeFactory scopes, ILogger log, string roomId, Duel.ResultWrite finished) {
        try {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DuelDb>();
            db.Matches.Add(new MatchRow {
                Id = finished.Id,
                RoomId = roomId,
                StartedAt = finished.StartedAt,
                FinishedAt = finished.FinishedAt,
                WavesPlayed = finished.WavesPlayed,
                HeartsLeft = finished.HeartsLeft,
                Winner = finished.Winner,
                Survivor = finished.Survivor,
                Waves = finished.Log.Select(wave => new WaveRow {
                    Number = wave.Number,
                    HeartsAfter = wave.HeartsAfter,
                    Damage = wave.Damage,
                    Slimes = wave.Slimes,
                    SlimesKilled = wave.SlimesKilled,
                    Groups = wave.Groups
                }).ToList()
            });
            await db.SaveChangesAsync();
        } catch (Exception error) {
            log.LogError(error, "Match {Match} was not saved", finished.Id);
        }
    }
}
