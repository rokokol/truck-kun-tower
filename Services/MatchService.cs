// Reading the past: a finished match is a row with its waves, and every endpoint that shows one
// goes through here so a nickname is resolved in one query instead of one per row.
public sealed class MatchService(IUnitOfWork unit) {
    public const int DefaultTake = 10, MaxTake = 50, MaxNote = 80;

    public async Task<List<MatchDto>> RecentAsync(int take = DefaultTake, CancellationToken token = default) =>
        await ToDtos(await unit.Matches.RecentAsync(Size(take), token), token);

    public async Task<List<MatchDto>> MineAsync(Guid me, int take = DefaultTake, CancellationToken token = default, int skip = 0) =>
        await ToDtos(await unit.Matches.ForUserAsync(me, Size(take), token, Math.Max(0, skip)), token);

    public async Task<MatchDetailDto> GetAsync(Guid id, CancellationToken token = default) {
        var match = await unit.Matches.ByIdAsync(id, token) ?? throw AppError.NotFound("Матч не найден");
        var names = await NamesAsync([match], token);
        return new MatchDetailDto(match.Id, match.RoomId, match.Winner, match.HeartsLeft, match.WavesPlayed,
            match.Note, match.StartedAt, match.FinishedAt,
            Brief(match.DefenderId, names), Brief(match.AttackerId, names),
            await unit.Matches.WavesAsync(id, token));
    }

    public async Task<string> ReplayAsync(Guid id, int? wave, CancellationToken token = default) =>
        (wave is int number ? await unit.Matches.ReplayWaveAsync(id, number, token)
            : await unit.Matches.ReplayMapAsync(id, token)) ?? throw AppError.NotFound("Запись не найдена");

    public async Task<StatsDto> StatsAsync(CancellationToken token = default) {
        var stats = await unit.Matches.StatsAsync(token);
        return new StatsDto(stats.Matches, stats.Sides, stats.AverageWaves, stats.AverageHeartsLeft,
            stats.KeepStanding, stats.Waves);
    }

    // The repository already puts the nickname on the row, so the leaderboard is a plain copy
    public async Task<List<LeaderDto>> LeaderboardAsync(int take = DefaultTake, CancellationToken token = default) =>
        (await unit.Matches.LeaderboardAsync(Size(take), token))
            .Select(row => new LeaderDto(row.UserId, row.Nickname, row.Played, row.Wins)).ToList();

    public async Task<MatchDto> SetNoteAsync(Guid me, Guid id, string? note, CancellationToken token = default) {
        var match = await Require(id, token);
        RequireParticipant(match, me);
        var text = (note ?? "").Trim();
        if (text.Length > MaxNote) throw AppError.BadRequest($"Заметка — до {MaxNote} символов");
        match.Note = text;
        await unit.SaveAsync(token);
        var names = await NamesAsync([match], token);
        return ToDto(match, names);
    }

    public async Task DeleteAsync(Guid me, Guid id, CancellationToken token = default) {
        var match = await Require(id, token);
        RequireParticipant(match, me);
        unit.Matches.Remove(match);
        await unit.SaveAsync(token);
    }

    static int Size(int take) => Math.Clamp(take, 1, MaxTake);

    async Task<MatchRow> Require(Guid id, CancellationToken token) =>
        await unit.Matches.ByIdAsync(id, token) ?? throw AppError.NotFound("Матч не найден");

    // A note and a delete belong to the two who played; a stranger learns nothing about the row
    static void RequireParticipant(MatchRow match, Guid me) {
        if (match.DefenderId != me && match.AttackerId != me)
            throw AppError.Forbidden("Этот матч не ваш");
    }

    async Task<List<MatchDto>> ToDtos(List<MatchRow> rows, CancellationToken token) {
        if (rows.Count == 0) return [];
        var names = await NamesAsync(rows, token);
        return rows.Select(row => ToDto(row, names)).ToList();
    }

    // One query for every nickname on the page: a list of ten matches must not cost ten lookups
    async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyCollection<MatchRow> rows, CancellationToken token) {
        var ids = rows.SelectMany(row => new[] { row.DefenderId, row.AttackerId })
            .OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0) return new();
        var users = await unit.Users.ByIdsAsync(ids, token);
        return users.ToDictionary(user => user.Id, user => user.Nickname);
    }

    static MatchDto ToDto(MatchRow row, Dictionary<Guid, string> names) =>
        new(row.Id, row.RoomId, row.Winner, row.HeartsLeft, row.WavesPlayed, row.Note,
            row.StartedAt, row.FinishedAt, Brief(row.DefenderId, names), Brief(row.AttackerId, names),
            row.ReplayMapJson is not null);

    static UserBrief? Brief(Guid? id, Dictionary<Guid, string> names) =>
        id is Guid userId ? new UserBrief(userId, names.GetValueOrDefault(userId, "")) : null;
}
