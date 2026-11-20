using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

// Who is asking: an account (JWT) or a guest holding the random seat token from their join.
public sealed record PlayerRef(Guid? UserId, string? SeatToken) {
    public bool IsGuest => UserId is null;
    public static readonly PlayerRef Nobody = new(null, null);
}

public static class PlayerIdentity {
    public const string SeatHeader = "X-Seat-Token";

    public static Guid? UserId(this ClaimsPrincipal principal) {
        var raw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static string Nickname(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtRegisteredClaimNames.Name)
        ?? principal.FindFirstValue(ClaimTypes.Name)
        ?? "";

    // A header, not a cookie: the game is served from one origin, but a cookie would ride along
    // with every static file and complicate the SignalR handshake
    public static PlayerRef Player(this HttpContext context) {
        var userId = context.User.UserId();
        var header = context.Request.Headers[SeatHeader].ToString();
        var token = string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        return new PlayerRef(userId, token);
    }
}
