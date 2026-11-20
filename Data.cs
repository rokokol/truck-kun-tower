using Microsoft.EntityFrameworkCore;

public sealed class MatchRow {
    public Guid Id { get; set; }
    public string RoomId { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int WavesPlayed { get; set; }
    public int HeartsLeft { get; set; }
    public string Winner { get; set; } = "";
    public bool Survivor { get; set; }
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
    public DbSet<MatchRow> Matches => Set<MatchRow>();
    public DbSet<WaveRow> Waves => Set<WaveRow>();

    protected override void OnModelCreating(ModelBuilder model) {
        model.Entity<MatchRow>().HasMany(match => match.Waves).WithOne(wave => wave.Match)
            .HasForeignKey(wave => wave.MatchId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MatchRow>().HasIndex(match => match.FinishedAt);
        model.Entity<WaveRow>().HasIndex(wave => new { wave.MatchId, wave.Number });
    }
}
