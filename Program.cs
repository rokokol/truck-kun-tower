using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    WebRootPath = Directory.Exists(publishedWebRoot) ? publishedWebRoot : null
});
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=duel.db";
var databaseFile = new SqliteConnectionStringBuilder(connection).DataSource;
if (!string.IsNullOrEmpty(databaseFile) && Path.GetDirectoryName(Path.GetFullPath(databaseFile)) is {} folder)
    Directory.CreateDirectory(folder);

builder.Services.AddSignalR();
builder.Services.AddDbContext<DuelDb>(options => options.UseSqlite(connection));
builder.Services.AddSingleton<TelegramBot>();
builder.Services.AddHostedService(services => services.GetRequiredService<TelegramBot>());
var app = builder.Build();
var rooms = new ConcurrentDictionary<string, Duel>();
using (var scope = app.Services.CreateScope()) {
    try {
        scope.ServiceProvider.GetRequiredService<DuelDb>().Database.EnsureCreated();
    } catch (Exception error) {
        app.Logger.LogError(error, "Database unavailable, matches will not be saved");
    }
}
app.UseDefaultFiles();
// A page never outlives a deploy: the markup and the sprites are revalidated on every load,
// so an updated header reaches a tab opened before the restart
app.UseStaticFiles(new StaticFileOptions {
    OnPrepareResponse = context => {
        context.Context.Response.GetTypedHeaders().CacheControl = context.File.Name.EndsWith(".html")
            ? new CacheControlHeaderValue { NoCache = true, MustRevalidate = true }
            : new CacheControlHeaderValue { MaxAge = TimeSpan.Zero, MustRevalidate = true };
    }
});
app.MapHub<Bell>("/bell");
// The browser asks where the Telegram app lives; the bot token never leaves the server
app.MapGet("/config", (TelegramBot bot) => Results.Ok(new { telegram = bot.PublicSettings }));
app.MapPost("/room", () => {
    var id = Guid.NewGuid().ToString("N");
    var game = rooms[id] = new Duel();
    return new { id, key = game.Key(0), seat = 0 };
});
app.MapPost("/room/{id}/join", async (string id, IHubContext<Bell> bell) => {
    if (!rooms.TryGetValue(id, out var g)) return Results.NotFound();
    lock (g) {
        if (!g.Join()) return Results.Conflict();
    }
    await bell.Clients.Group(id).SendAsync("changed");
    return Results.Ok(new { id, key = g.Key(1), seat = 1 });
});
app.MapPost("/room/{id}/state", (string id, Ticket t) => {
    if (!rooms.TryGetValue(id, out var g)) return Results.NotFound();
    lock (g) return g.Seat(t.Key) is var i && i >= 0 ? Results.Ok(g.View(i)) : Results.Unauthorized();
});
app.MapPost("/room/{id}/move", async (string id, Move m, IHubContext<Bell> bell, IServiceScopeFactory scopes) => {
    if (!rooms.TryGetValue(id, out var g)) return Results.NotFound();
    object state;
    Duel.ResultWrite? finished;
    lock (g) {
        int i = g.Seat(m.Key);
        if (i < 0) return Results.Unauthorized();
        if (g.Apply(i, m) is {} rejected) return rejected;
        state = g.View(i);
        finished = g.TakeResult();
    }
    if (finished is not null) await Save(id, finished);
    await bell.Clients.Group(id).SendAsync("changed");
    return Results.Ok(state);
});
// LINQ over the database: these queries translate to SQL, they do not run in memory
app.MapGet("/stats", async (DuelDb db) => {
    var matches = await db.Matches.CountAsync();
    var sides = await db.Matches.GroupBy(match => match.Winner)
        .Select(group => new { side = group.Key, wins = group.Count() })
        .OrderByDescending(row => row.wins).ToListAsync();
    var settled = db.Matches.Where(match => match.FinishedAt != default);
    var waves = await db.Waves.GroupBy(wave => wave.Number)
        .Select(group => new {
            wave = group.Key,
            damage = group.Sum(wave => wave.Damage),
            slimes = group.Sum(wave => wave.Slimes),
            killed = group.Sum(wave => wave.SlimesKilled)
        }).OrderBy(row => row.wave).ToListAsync();
    return Results.Ok(new {
        matches,
        sides,
        averageWaves = matches == 0 ? 0 : await settled.AverageAsync(match => (double)match.WavesPlayed),
        averageHeartsLeft = matches == 0 ? 0 : await settled.AverageAsync(match => (double)match.HeartsLeft),
        keepStanding = await settled.CountAsync(match => match.Survivor),
        waves
    });
});
app.MapGet("/matches", async (DuelDb db, int? take) => {
    var limit = Math.Clamp(take ?? 10, 1, 50);
    var recent = await db.Matches.OrderByDescending(match => match.FinishedAt).Take(limit)
        .Select(match => new {
            match.Id, match.RoomId, match.Winner, match.HeartsLeft, match.WavesPlayed,
            match.StartedAt, match.FinishedAt
        }).ToListAsync();
    return Results.Ok(recent);
});
app.Run();

async Task Save(string roomId, Duel.ResultWrite finished) {
    try {
        await using var scope = app.Services.CreateAsyncScope();
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
        app.Logger.LogError(error, "Match {Match} was not saved", finished.Id);
    }
}

public record Ticket(string Key);
public record Move(string? Key, int Wave, string? Action,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int? Cell);
public class Bell : Hub {
    public Task Watch(string id) => Groups.AddToGroupAsync(Context.ConnectionId, id);
}
