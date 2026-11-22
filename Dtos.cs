// The wire contract: every controller answers with one of these, never with an entity.
// The browser talks to these shapes, so a field renamed here is a client change too.

public record UserBrief(Guid Id, string Nickname);
public record OnlineDto(int Count, List<UserBrief> Players);

public record UserDto(Guid Id, string Nickname, DateTime CreatedAt);
public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);
public record RegisterRequest(string? Nickname, string? Password);
public record LoginRequest(string? Nickname, string? Password);
public record UpdateProfileRequest(string? Nickname, string? CurrentPassword, string? NewPassword);
public record DeleteAccountRequest(string? Password);

public record FriendDto(Guid Id, string Nickname, bool Online, DateTime FriendsSince);
public record IncomingRequestDto(Guid Id, UserBrief From, DateTime CreatedAt);
public record OutgoingRequestDto(Guid Id, UserBrief To, DateTime CreatedAt);
public record RequestListDto(List<IncomingRequestDto> Incoming, List<OutgoingRequestDto> Outgoing);
public record FriendRequestCreate(string? Nickname);

public record InviteDto(Guid Id, UserBrief From, string RoomId, DateTime CreatedAt, DuelSession? Session = null);
public record InviteCreate(Guid? FriendId, string? RoomId);
public record InviteAccepted(string RoomId);

public record CreateDuelRequest(string? Side, bool RandomOpponent = false);
public record JoinDuelRequest(string? Side);
public record RoomDto(string Id, string MySide, UserBrief? Opponent, string Status,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, Guid? ReplayId = null);
public record DuelSession(string Id, string Side, string? SeatToken, RoomDto Room);
public record SideRequest(string? Side);

public record MatchDto(Guid Id, string RoomId, string Winner, int HeartsLeft, int WavesPlayed,
    string Note, DateTime StartedAt, DateTime FinishedAt, UserBrief? Defender, UserBrief? Attacker, bool HasReplay);
public record MatchDetailDto(Guid Id, string RoomId, string Winner, int HeartsLeft, int WavesPlayed,
    string Note, DateTime StartedAt, DateTime FinishedAt, UserBrief? Defender, UserBrief? Attacker,
    List<WaveDto> Waves);
public record WaveDto(int Number, int HeartsAfter, int Damage, int Slimes, int SlimesKilled, int Groups);
public record NoteRequest(string? Note);
public record StatsDto(int Matches, List<SideWins> Sides, double AverageWaves, double AverageHeartsLeft,
    int KeepStanding, List<WaveStat> Waves);
public record LeaderDto(Guid Id, string Nickname, int Played, int Wins);

// A move keeps its cell strict: a boolean or a string here used to slip through as "the first cell"
public record Move(int Wave, string? Action,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)] int? Cell);

// Sides travel as words: the client should not have to know that the defender is zero
public static class Sides {
    public const int Defender = 0, Attacker = 1;
    public static int? Parse(string? side) => side?.Trim().ToLowerInvariant() switch {
        "defender" or "defence" or "defense" => Defender,
        "attacker" or "attack" => Attacker,
        _ => null
    };
    public static string Name(int side) => side == Defender ? "defender" : "attacker";
    public static string Russian(int side) => side == Defender ? "защитник" : "атакующий";
}
