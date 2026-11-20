using Microsoft.EntityFrameworkCore;

// The durable side of the game: accounts, friendships, invitations, rooms and finished matches.
// A live board stays in memory (Duel.cs); a row here says who is in the room and how it ended.

public enum FriendshipStatus { Pending, Accepted }
public enum InviteStatus { Pending, Accepted, Declined, Cancelled, Expired }
public enum RoomStatus { Waiting, Playing, Finished, Cancelled, Abandoned }

public sealed class User {
    public Guid Id { get; set; }
    public string Nickname { get; set; } = "";
    // Lower-cased nickname: the unique index is what keeps two "Иван" apart
    public string NicknameKey { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public List<Friendship> SentRequests { get; set; } = [];
    public List<Friendship> ReceivedRequests { get; set; } = [];
}

public sealed class Friendship {
    public Guid Id { get; set; }
    public Guid RequesterId { get; set; }
    public Guid AddresseeId { get; set; }
    public FriendshipStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    public User? Requester { get; set; }
    public User? Addressee { get; set; }
}

public sealed class DuelInvite {
    public Guid Id { get; set; }
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string RoomId { get; set; } = "";
    public InviteStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public User? From { get; set; }
    public User? To { get; set; }
}

public sealed class Room {
    public string Id { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public Guid? DefenderId { get; set; }
    public Guid? AttackerId { get; set; }
    public RoomStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed class MatchRow {
    public Guid Id { get; set; }
    public string RoomId { get; set; } = "";
    public Guid? DefenderId { get; set; }
    public Guid? AttackerId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int WavesPlayed { get; set; }
    public int HeartsLeft { get; set; }
    public string Winner { get; set; } = "";
    public bool Survivor { get; set; }
    // A participant's own label for the series: "реванш", "финал", whatever
    public string Note { get; set; } = "";
    public List<WaveRow> Waves { get; set; } = [];
}

public sealed class WaveRow {
    public int Id { get; set; }
    public Guid MatchId { get; set; }
    public int Number { get; set; }
    public int HeartsAfter { get; set; }
    public int Damage { get; set; }
    public int Slimes { get; set; }
    public int SlimesKilled { get; set; }
    public int Groups { get; set; }
    public MatchRow? Match { get; set; }
}

public sealed class DuelDb(DbContextOptions<DuelDb> options) : DbContext(options) {
    public DbSet<User> Users => Set<User>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<DuelInvite> DuelInvites => Set<DuelInvite>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<MatchRow> Matches => Set<MatchRow>();
    public DbSet<WaveRow> Waves => Set<WaveRow>();

    protected override void OnModelCreating(ModelBuilder model) {
        model.Entity<User>(user => {
            user.HasIndex(entry => entry.NicknameKey).IsUnique();
            user.Property(entry => entry.Nickname).HasMaxLength(24);
            user.Property(entry => entry.NicknameKey).HasMaxLength(24);
            user.Property(entry => entry.PasswordHash).HasMaxLength(160);
        });

        model.Entity<Friendship>(friendship => {
            // One row per pair, whichever way the first request went
            friendship.HasIndex(entry => new { entry.RequesterId, entry.AddresseeId }).IsUnique();
            friendship.HasOne(entry => entry.Requester).WithMany(user => user.SentRequests)
                .HasForeignKey(entry => entry.RequesterId).OnDelete(DeleteBehavior.Cascade);
            friendship.HasOne(entry => entry.Addressee).WithMany(user => user.ReceivedRequests)
                .HasForeignKey(entry => entry.AddresseeId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DuelInvite>(invite => {
            invite.HasIndex(entry => new { entry.ToId, entry.Status });
            invite.HasIndex(entry => new { entry.FromId, entry.ToId, entry.RoomId, entry.Status });
            invite.HasOne(entry => entry.From).WithMany()
                .HasForeignKey(entry => entry.FromId).OnDelete(DeleteBehavior.Cascade);
            invite.HasOne(entry => entry.To).WithMany()
                .HasForeignKey(entry => entry.ToId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Room>(room => {
            room.HasKey(entry => entry.Id);
            room.Property(entry => entry.Id).HasMaxLength(32);
            room.HasIndex(entry => entry.Status);
            room.HasIndex(entry => entry.CreatedAt);
        });

        model.Entity<MatchRow>(match => {
            match.HasIndex(entry => entry.FinishedAt);
            match.HasIndex(entry => entry.RoomId);
            match.HasIndex(entry => new { entry.DefenderId, entry.FinishedAt });
            match.HasIndex(entry => new { entry.AttackerId, entry.FinishedAt });
            match.HasMany(entry => entry.Waves).WithOne(wave => wave.Match)
                .HasForeignKey(wave => wave.MatchId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<WaveRow>().HasIndex(wave => new { wave.MatchId, wave.Number });
    }
}
