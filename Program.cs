using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    WebRootPath = Directory.Exists(publishedWebRoot) ? publishedWebRoot : null
});
builder.Services.AddSignalR();
var app = builder.Build();
var rooms = new ConcurrentDictionary<string, Duel>();
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
app.MapPost("/room/{id}/move", async (string id, Move m, IHubContext<Bell> bell) => {
    if (!rooms.TryGetValue(id, out var g)) return Results.NotFound();
    object state;
    lock (g) {
        int i = g.Seat(m.Key);
        if (i < 0) return Results.Unauthorized();
        if (g.Apply(i, m) is {} rejected) return rejected;
        state = g.View(i);
    }
    await bell.Clients.Group(id).SendAsync("changed");
    return Results.Ok(state);
});
app.Run();
public record Ticket(string Key);
public record Move(string? Key, int Wave, string? Action,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int? Cell);
public class Bell : Hub {
    public Task Watch(string id) => Groups.AddToGroupAsync(Context.ConnectionId, id);
}
