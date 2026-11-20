using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    WebRootPath = Directory.Exists(publishedWebRoot) ? publishedWebRoot : null
});

// The default suits a compose run on one machine; a deployment passes its own connection string
var connection = builder.Configuration.GetConnectionString("Default")
    ?? "Host=127.0.0.1;Port=5432;Database=tower;Username=tower;Password=tower";

builder.Services.AddSignalR();
builder.Services.AddControllers().AddJsonOptions(options => {
    // The browser sends "defender" and reads "Waiting", so enums travel as names, not numbers
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddSingleton<Rooms>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<FriendService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddHostedService<WaveClock>();
builder.Services.AddDbContext<DuelDb>(options => options.UseNpgsql(connection));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TokenService>((options, tokens) => {
        options.TokenValidationParameters = tokens.Validation;
        options.Events = new JwtBearerEvents {
            // A websocket cannot set an Authorization header, so SignalR carries the token in the query
            OnMessageReceived = context => {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/bell"))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope()) {
    var db = scope.ServiceProvider.GetRequiredService<DuelDb>();
    for (var attempt = 1; ; attempt++) {
        try {
            await db.Database.MigrateAsync();
            break;
        } catch (Exception error) when (attempt < 10) {
            app.Logger.LogWarning(error, "PostgreSQL is not ready yet, retrying ({Attempt}/10)", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
    // A board lives in memory, so a room from before this start can never be played again
    var dead = await db.Rooms
        .Where(room => room.Status == RoomStatus.Waiting || room.Status == RoomStatus.Playing).ToListAsync();
    foreach (var room in dead) room.Status = RoomStatus.Abandoned;
    var stale = await db.DuelInvites.Where(invite => invite.Status == InviteStatus.Pending).ToListAsync();
    foreach (var invite in stale) {
        invite.Status = InviteStatus.Expired;
        invite.ResolvedAt = DateTime.UtcNow;
    }
    if (dead.Count + stale.Count > 0) await db.SaveChangesAsync();
}

app.UseAppErrors();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<Bell>("/bell");
app.Run();
