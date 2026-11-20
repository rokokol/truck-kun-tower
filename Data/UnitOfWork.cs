using Microsoft.EntityFrameworkCore;

// Repository + UnitOfWork: services never touch DuelDb, they talk to these interfaces.
// Queries stay in EF's IQueryable so they run in PostgreSQL, not in memory.

public sealed record SideWins(string Side, int Wins);
public sealed record WaveStat(int Wave, int Damage, int Slimes, int Killed);
public sealed record MatchStats(int Matches, List<SideWins> Sides, double AverageWaves,
    double AverageHeartsLeft, int KeepStanding, List<WaveStat> Waves);
public sealed record LeaderRow(Guid UserId, string Nickname, int Played, int Wins);

public interface IUserRepository {
    Task<User?> ByIdAsync(Guid id, CancellationToken token = default);
    Task<User?> ByNicknameAsync(string nickname, CancellationToken token = default);
    Task<List<User>> ByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken token = default);
    Task<bool> NicknameTakenAsync(string nickname, CancellationToken token = default);
    void Add(User user);
    void Remove(User user);
}

public interface IFriendshipRepository {
    Task<Friendship?> ByIdAsync(Guid id, CancellationToken token = default);
    Task<Friendship?> BetweenAsync(Guid first, Guid second, CancellationToken token = default);
    Task<List<Friendship>> ForAsync(Guid userId, FriendshipStatus? status = null, CancellationToken token = default);
    void Add(Friendship friendship);
    void Remove(Friendship friendship);
}

public interface IInviteRepository {
    Task<DuelInvite?> ByIdAsync(Guid id, CancellationToken token = default);
    Task<List<DuelInvite>> IncomingAsync(Guid toId, CancellationToken token = default);
    Task<bool> PendingExistsAsync(Guid fromId, Guid toId, string roomId, CancellationToken token = default);
    void Add(DuelInvite invite);
}

public interface IRoomRepository {
    Task<Room?> ByIdAsync(string id, CancellationToken token = default);
    Task<List<Room>> ForUserAsync(Guid userId, int take = 20, CancellationToken token = default);
    Task<List<Room>> LiveAsync(CancellationToken token = default);
    void Add(Room room);
}

public interface IMatchRepository {
    Task<MatchRow?> ByIdAsync(Guid id, CancellationToken token = default);
    Task<List<MatchRow>> RecentAsync(int take, CancellationToken token = default);
    Task<List<MatchRow>> ForUserAsync(Guid userId, int take, CancellationToken token = default);
    Task<MatchStats> StatsAsync(CancellationToken token = default);
    Task<List<LeaderRow>> LeaderboardAsync(int take, CancellationToken token = default);
    void Add(MatchRow match);
    void Remove(MatchRow match);
}

public interface IUnitOfWork {
    IUserRepository Users { get; }
    IFriendshipRepository Friendships { get; }
    IInviteRepository Invites { get; }
    IRoomRepository Rooms { get; }
    IMatchRepository Matches { get; }
    Task<int> SaveAsync(CancellationToken token = default);
}

// One place for the nickname rule: trimming and lower-casing decide who is the same person
public static class Nicknames {
    public const int MaxLength = 24;
    public static string Normalize(string nickname) => nickname.Trim().ToLowerInvariant();
    public static string? Validate(string? nickname) {
        var trimmed = nickname?.Trim() ?? "";
        if (trimmed.Length is < 3 or > MaxLength) return $"Ник — от 3 до {MaxLength} символов";
        if (trimmed.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))) return "В нике можно буквы, цифры, _, - и .";
        return null;
    }
}

abstract class Repository<T>(DuelDb db) where T : class {
    protected DuelDb Db { get; } = db;
    protected DbSet<T> Set { get; } = db.Set<T>();
}

sealed class UserRepository(DuelDb db) : Repository<User>(db), IUserRepository {
    public Task<User?> ByIdAsync(Guid id, CancellationToken token = default) =>
        Set.FirstOrDefaultAsync(user => user.Id == id, token);

    public Task<User?> ByNicknameAsync(string nickname, CancellationToken token = default) {
        var key = Nicknames.Normalize(nickname);
        return Set.FirstOrDefaultAsync(user => user.NicknameKey == key, token);
    }

    public async Task<List<User>> ByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken token = default) {
        if (ids.Count == 0) return [];
        return await Set.Where(user => ids.Contains(user.Id)).ToListAsync(token);
    }

    public Task<bool> NicknameTakenAsync(string nickname, CancellationToken token = default) {
        var key = Nicknames.Normalize(nickname);
        return Set.AnyAsync(user => user.NicknameKey == key, token);
    }

    public void Add(User user) => Set.Add(user);
    public void Remove(User user) => Set.Remove(user);
}

sealed class FriendshipRepository(DuelDb db) : Repository<Friendship>(db), IFriendshipRepository {
    public Task<Friendship?> ByIdAsync(Guid id, CancellationToken token = default) =>
        Set.Include(row => row.Requester).Include(row => row.Addressee)
            .FirstOrDefaultAsync(row => row.Id == id, token);

    public Task<Friendship?> BetweenAsync(Guid first, Guid second, CancellationToken token = default) =>
        Set.Include(row => row.Requester).Include(row => row.Addressee)
            .FirstOrDefaultAsync(row => (row.RequesterId == first && row.AddresseeId == second) ||
                (row.RequesterId == second && row.AddresseeId == first), token);

    public async Task<List<Friendship>> ForAsync(Guid userId, FriendshipStatus? status = null, CancellationToken token = default) {
        var query = Set.Include(row => row.Requester).Include(row => row.Addressee)
            .Where(row => row.RequesterId == userId || row.AddresseeId == userId);
        if (status is { } wanted) query = query.Where(row => row.Status == wanted);
        return await query.ToListAsync(token);
    }

    public void Add(Friendship friendship) => Set.Add(friendship);
    public void Remove(Friendship friendship) => Set.Remove(friendship);
}

sealed class InviteRepository(DuelDb db) : Repository<DuelInvite>(db), IInviteRepository {
    public Task<DuelInvite?> ByIdAsync(Guid id, CancellationToken token = default) =>
        Set.Include(row => row.From).Include(row => row.To)
            .FirstOrDefaultAsync(row => row.Id == id, token);

    public async Task<List<DuelInvite>> IncomingAsync(Guid toId, CancellationToken token = default) =>
        await Set.Include(row => row.From)
            .Where(row => row.ToId == toId && row.Status == InviteStatus.Pending)
            .OrderByDescending(row => row.CreatedAt).ToListAsync(token);

    public Task<bool> PendingExistsAsync(Guid fromId, Guid toId, string roomId, CancellationToken token = default) =>
        Set.AnyAsync(row => row.FromId == fromId && row.ToId == toId && row.RoomId == roomId &&
            row.Status == InviteStatus.Pending, token);

    public void Add(DuelInvite invite) => Set.Add(invite);
}

sealed class RoomRepository(DuelDb db) : Repository<Room>(db), IRoomRepository {
    public Task<Room?> ByIdAsync(string id, CancellationToken token = default) =>
        Set.FirstOrDefaultAsync(room => room.Id == id, token);

    public async Task<List<Room>> ForUserAsync(Guid userId, int take = 20, CancellationToken token = default) =>
        await Set.Where(room => room.OwnerId == userId || room.DefenderId == userId || room.AttackerId == userId)
            .OrderByDescending(room => room.CreatedAt).Take(take).ToListAsync(token);

    public async Task<List<Room>> LiveAsync(CancellationToken token = default) =>
        await Set.Where(room => room.Status == RoomStatus.Waiting || room.Status == RoomStatus.Playing)
            .ToListAsync(token);

    public void Add(Room room) => Set.Add(room);
}

sealed class MatchRepository(DuelDb db) : Repository<MatchRow>(db), IMatchRepository {
    public Task<MatchRow?> ByIdAsync(Guid id, CancellationToken token = default) =>
        Set.Include(match => match.Waves).FirstOrDefaultAsync(match => match.Id == id, token);

    public async Task<List<MatchRow>> RecentAsync(int take, CancellationToken token = default) =>
        await Set.AsNoTracking().OrderByDescending(match => match.FinishedAt).Take(take).ToListAsync(token);

    public async Task<List<MatchRow>> ForUserAsync(Guid userId, int take, CancellationToken token = default) =>
        await Set.AsNoTracking()
            .Where(match => match.DefenderId == userId || match.AttackerId == userId)
            .OrderByDescending(match => match.FinishedAt).Take(take).ToListAsync(token);

    public async Task<MatchStats> StatsAsync(CancellationToken token = default) {
        var total = await Set.CountAsync(token);
        var sides = await Set.GroupBy(match => match.Winner)
            .Select(group => new { side = group.Key, wins = group.Count() })
            .OrderByDescending(row => row.wins).ToListAsync(token);
        var settled = Set.Where(match => match.FinishedAt != default);
        var averageWaves = total == 0 ? 0 : await settled.AverageAsync(match => (double)match.WavesPlayed, token);
        var averageHearts = total == 0 ? 0 : await settled.AverageAsync(match => (double)match.HeartsLeft, token);
        var standing = await settled.CountAsync(match => match.Survivor, token);
        var waves = await Db.Waves.GroupBy(wave => wave.Number)
            .Select(group => new {
                wave = group.Key,
                damage = group.Sum(wave => wave.Damage),
                slimes = group.Sum(wave => wave.Slimes),
                killed = group.Sum(wave => wave.SlimesKilled)
            }).OrderBy(row => row.wave).ToListAsync(token);
        return new MatchStats(total, sides.Select(row => new SideWins(row.side, row.wins)).ToList(),
            averageWaves, averageHearts, standing,
            waves.Select(row => new WaveStat(row.wave, row.damage, row.slimes, row.killed)).ToList());
    }

    public async Task<List<LeaderRow>> LeaderboardAsync(int take, CancellationToken token = default) {
        var asDefender = await Set.Where(match => match.DefenderId != null)
            .GroupBy(match => match.DefenderId!.Value)
            .Select(group => new {
                userId = group.Key,
                played = group.Count(),
                wins = group.Count(match => match.Winner == "Defender")
            }).ToListAsync(token);
        var asAttacker = await Set.Where(match => match.AttackerId != null)
            .GroupBy(match => match.AttackerId!.Value)
            .Select(group => new {
                userId = group.Key,
                played = group.Count(),
                wins = group.Count(match => match.Winner == "Attacker")
            }).ToListAsync(token);
        var totals = asDefender.Select(row => new LeaderRow(row.userId, "", row.played, row.wins))
            .Concat(asAttacker.Select(row => new LeaderRow(row.userId, "", row.played, row.wins)))
            .GroupBy(row => row.UserId)
            .Select(group => new LeaderRow(group.Key, "", group.Sum(row => row.Played), group.Sum(row => row.Wins)))
            .OrderByDescending(row => row.Wins).ThenByDescending(row => row.Played).ThenBy(row => row.UserId)
            .Take(take).ToList();
        var ids = totals.Select(row => row.UserId).ToList();
        if (ids.Count == 0) return totals;
        var names = await Db.Users.Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Nickname }).ToListAsync(token);
        var byId = names.ToDictionary(row => row.Id, row => row.Nickname);
        return totals.Select(row => row with { Nickname = byId.GetValueOrDefault(row.UserId, "") }).ToList();
    }

    public void Add(MatchRow match) => Set.Add(match);
    public void Remove(MatchRow match) => Set.Remove(match);
}

public sealed class UnitOfWork(DuelDb db) : IUnitOfWork {
    public IUserRepository Users { get; } = new UserRepository(db);
    public IFriendshipRepository Friendships { get; } = new FriendshipRepository(db);
    public IInviteRepository Invites { get; } = new InviteRepository(db);
    public IRoomRepository Rooms { get; } = new RoomRepository(db);
    public IMatchRepository Matches { get; } = new MatchRepository(db);

    public Task<int> SaveAsync(CancellationToken token = default) => db.SaveChangesAsync(token);
}
