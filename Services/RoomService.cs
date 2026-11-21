using Microsoft.AspNetCore.SignalR;

// Who may sit where, and which rows follow a live board. A board is memory and a row is history,
// so this is the only place that keeps the two saying the same thing.
public sealed class RoomService(
    Rooms rooms,
    IUnitOfWork unit,
    IHubContext<Bell> bell,
    IServiceScopeFactory scopes,
    ILogger<RoomService> log) {

    // The browser redraws on this event; renaming it would be a client change too
    const string ChangedEvent = "Changed";

    public Task<DuelSession> CreateAsync(PlayerRef who, int side, CancellationToken token = default, bool randomOpponent = false) =>
        WithMembership(() => CreateCoreAsync(who, side, token, randomOpponent), token);

    async Task<DuelSession> CreateCoreAsync(PlayerRef who, int side, CancellationToken token, bool randomOpponent) {
        if (randomOpponent && who.IsGuest) throw AppError.Unauthorized("Нужен вход");
        rooms.RequireAvailable(who);
        var id = Rooms.NewId();
        var game = new Duel { RandomOpponent = randomOpponent };
        if (!game.TryTake(who, side, out var seat)) throw AppError.Conflict("Не удалось занять это место");
        if (!rooms.Add(id, game)) throw AppError.Conflict("Комната не открылась, попробуйте ещё раз");

        Room? row = null;
        if (who.UserId is Guid owner) {
            row = new Room {
                Id = id,
                OwnerId = owner,
                DefenderId = side == Duel.Defender ? owner : null,
                AttackerId = side == Duel.Attacker ? owner : null,
                Status = RoomStatus.Waiting,
                CreatedAt = DateTime.UtcNow
            };
            unit.Rooms.Add(row);
            try { await unit.SaveAsync(token); }
            catch { rooms.Remove(id, out _); throw; }
        } else {
            // A guest room is a link and a board: nothing to remember once the tab is closed
            log.LogDebug("Room {Room} is a guest room and stays out of the database", id);
        }

        var opponent = await BriefAsync(game.UserAt(1 - side), token);
        return new DuelSession(id, Sides.Name(side), seat.Token, Card(id, row, side, opponent, game));
    }

    // Coming back to a room you already sit in is a reload, not a second player
    public Task<DuelSession> JoinAsync(PlayerRef who, string id, int? side = null, CancellationToken token = default) =>
        WithMembership(() => JoinCoreAsync(who, id, side, token), token);

    public Task<DuelSession?> JoinRandomAsync(Guid userId, CancellationToken token = default) =>
        WithMembership<DuelSession?>(async () => {
            var who = new PlayerRef(userId, null);
            rooms.RequireAvailable(who);
            while (true) {
                var open = rooms.All.Where(pair => {
                    lock (pair.Value) return pair.Value.CanMatch;
                }).OrderByDescending(pair => pair.Value.CreationOrder).Select(pair => pair.Key).FirstOrDefault();
                if (open is null) return null;
                try { return await JoinCoreAsync(who, open, null, token, fromPool: true); }
                catch (AppError error) when (error.Code == "no_random_duel") { }
            }
        }, token);

    async Task<DuelSession> JoinCoreAsync(PlayerRef who, string id, int? side, CancellationToken token, bool fromPool = false) {
        var game = Require(id);
        rooms.RequireAvailable(who, id);
        Duel.Seat seat;
        int mine;
        Guid[]? started = null;
        lock (game) {
            if (fromPool && !game.CanMatch)
                throw new AppError("Нет открытых дуэлей", 404, "no_random_duel");
            mine = game.SeatOf(who);
            if (mine >= 0) {
                seat = game.SeatAt(mine)!;
            } else {
                // No side asked for means "wherever there is room", so an invite link needs no query
                var wanted = side ?? (game.SeatAt(Duel.Defender) is null ? Duel.Defender : Duel.Attacker);
                if (!game.TryTake(who, wanted, out seat)) throw AppError.Conflict("Место занято");
                mine = wanted;
                if (game.Joined) started = PlayersIn(game);
            }
        }

        var row = await FollowAsync(id, game, who, started, token);
        await AnnounceAsync(id, token);
        var opponent = await BriefAsync(game.UserAt(1 - mine), token);
        return new DuelSession(id, Sides.Name(mine), seat.Token, Card(id, row, mine, opponent, game));
    }

    public Task<object> StateAsync(PlayerRef who, string id, CancellationToken token = default) =>
        ViewAsync(Require(id), who, token, poll: true);

    public async Task<RoomDto> RoomAsync(PlayerRef who, string id, CancellationToken token = default) {
        var game = Require(id);
        int seat;
        lock (game) seat = SeatOrThrow(game, who);
        var row = await unit.Rooms.ByIdAsync(id, token);
        var opponent = await BriefAsync(game.UserAt(1 - seat), token);
        return Card(id, row, seat, opponent, game);
    }

    public Task<object> MoveAsync(PlayerRef who, string id, Move move, CancellationToken token = default) =>
        move.Action == "rematch" ? WithMembership(() => MoveCoreAsync(who, id, move, token), token)
            : MoveCoreAsync(who, id, move, token);

    async Task<object> MoveCoreAsync(PlayerRef who, string id, Move move, CancellationToken token) {
        var game = Require(id);
        int seat;
        IResult? refused;
        Duel.ResultWrite? finished;
        bool publicChange;
        lock (game) {
            seat = SeatOrThrow(game, who);
            if (rooms.Get(id) != game) throw AppError.NotFound("Комната не найдена");
            if (move.Action == "rematch" && game.Done) {
                foreach (var side in new[] {Duel.Defender, Duel.Attacker}) {
                    if (game.SeatAt(side) is { } chair)
                        rooms.RequireAvailable(new PlayerRef(chair.UserId, chair.Token), id);
                }
            }
            int previous = game.ResolvedWaves;
            refused = game.Apply(seat, move);
            publicChange = game.ResolvedWaves != previous;
            // A rematch clears the board without a result, so the result is asked for either way
            finished = game.TakeResult();
        }
        if (refused is not null) throw Refusal(refused);
        if (finished is not null) await Storage.SaveAsync(scopes, log, id, finished);
        // A room notification would reveal when the opponent submits a private order.
        if (publicChange) await AnnounceAsync(id, token);
        return await ViewAsync(game, who, token);
    }

    public async Task<object> AskSwapAsync(PlayerRef who, string id, CancellationToken token = default) {
        var game = Require(id);
        lock (game) {
            var seat = SeatOrThrow(game, who);
            if (!game.AskSwitch(seat)) throw AppError.Conflict("Сейчас стороны менять нельзя");
        }
        await SyncSidesAsync(id, game, token);
        await AnnounceAsync(id, token);
        return await ViewAsync(game, who, token);
    }

    public async Task<object> AnswerSwapAsync(PlayerRef who, string id, bool accept, CancellationToken token = default) {
        var game = Require(id);
        lock (game) {
            var seat = SeatOrThrow(game, who);
            if (!game.AnswerSwitch(seat, accept)) throw AppError.Conflict("Запроса нет");
        }
        await SyncSidesAsync(id, game, token);
        await AnnounceAsync(id, token);
        return await ViewAsync(game, who, token);
    }

    public Task CancelAsync(PlayerRef who, string id, CancellationToken token = default) =>
        WithMembership(() => CancelCoreAsync(who, id), token);

    async Task<bool> CancelCoreAsync(PlayerRef who, string id) {
        var game = Require(id);
        lock (game) {
            SeatOrThrow(game, who);
            rooms.Remove(id, out _);
        }

        var row = await unit.Rooms.ByIdAsync(id);
        if (row is not null && !game.Done) {
            row.Status = RoomStatus.Cancelled;
            row.FinishedAt = DateTime.UtcNow;
            await unit.SaveAsync();
        }
        await AnnounceAsync(id, CancellationToken.None);
        return true;
    }

    // "My duels" outlives the boards: a live one answers with its own status, the rest with the row's
    public async Task<List<RoomDto>> MineAsync(Guid me, CancellationToken token = default, int take = 20, int skip = 0) {
        var rows = await unit.Rooms.ForUserAsync(me, Math.Clamp(take, 1, 50), token, Math.Max(0, skip));
        var rivals = rows.Select(row => RivalOf(row, me)).OfType<Guid>().Distinct().ToList();
        var names = rivals.Count == 0
            ? new Dictionary<Guid, string>()
            : (await unit.Users.ByIdsAsync(rivals, token)).ToDictionary(user => user.Id, user => user.Nickname);

        var entries = rows.Select(row => (Row: row, Status: rooms.Get(row.Id) is { } live
            ? StatusOf(live) : row.Status)).ToArray();
        var closed = entries.Where(entry => entry.Status is RoomStatus.Finished or RoomStatus.Cancelled
            or RoomStatus.Abandoned or RoomStatus.Interrupted)
            .Select(entry => entry.Row.Id).ToArray();
        var replays = closed.Length == 0 ? new Dictionary<string, Guid?>()
            : await unit.Matches.LatestReplayIdsAsync(me, closed, token);

        return entries.Select(entry => {
            var (row, status) = entry;
            var side = row.DefenderId == me ? Duel.Defender : row.AttackerId == me ? Duel.Attacker : -1;
            var rival = RivalOf(row, me);
            return new RoomDto(row.Id, side < 0 ? "" : Sides.Name(side),
                rival is Guid id ? new UserBrief(id, names.GetValueOrDefault(id, "")) : null,
                status.ToString(), row.CreatedAt, row.StartedAt, row.FinishedAt, replays.GetValueOrDefault(row.Id));
        }).ToList();
    }

    // The row follows the board: a guest owns no column, and a full pair means the game is on
    static Guid[] PlayersIn(Duel game) => new[] {game.UserAt(Duel.Defender), game.UserAt(Duel.Attacker)}.OfType<Guid>().ToArray();

    async Task<Room?> FollowAsync(string id, Duel game, PlayerRef who, Guid[]? started, CancellationToken token) {
        var row = await unit.Rooms.ByIdAsync(id, token);
        if (row is null) {
            if (who.UserId is not Guid owner) return null;
            row = new Room { Id = id, OwnerId = owner, CreatedAt = DateTime.UtcNow };
            unit.Rooms.Add(row);
        }
        lock (game) {
            row.DefenderId = game.UserAt(Duel.Defender);
            row.AttackerId = game.UserAt(Duel.Attacker);
            if (game.Joined) {
                row.Status = RoomStatus.Playing;
                // The clock starts for the pair, so a row that already has a start keeps it
                row.StartedAt ??= DateTime.UtcNow;
            } else if (row.Status is RoomStatus.Waiting or RoomStatus.Playing) {
                row.Status = RoomStatus.Waiting;
            }
        }
        var closed = started is {Length: > 0} ? await InviteService.CancelPendingAsync(unit, id, started, token) : [];
        await unit.SaveAsync(token);
        await InviteService.NotifyCancelledAsync(bell, closed);
        return row;
    }

    // A swap moves the accounts between the columns, so the row keeps saying who sits where
    async Task SyncSidesAsync(string id, Duel game, CancellationToken token) {
        var row = await unit.Rooms.ByIdAsync(id, token);
        if (row is null) return;
        lock (game) {
            row.DefenderId = game.UserAt(Duel.Defender);
            row.AttackerId = game.UserAt(Duel.Attacker);
        }
        await unit.SaveAsync(token);
    }

    async Task<object> ViewAsync(Duel game, PlayerRef who, CancellationToken token, bool poll = false) {
        int seat;
        lock (game) seat = SeatOrThrow(game, who);
        // The nickname comes from the database, so it is read outside the lock; a swap in that
        // window only labels the names of a board nobody else can see yet
        var opponent = await BriefAsync(game.UserAt(1 - seat), token);
        lock (game) {
            seat = SeatOrThrow(game, who);
            if (poll) game.Poll(seat, DateTime.UtcNow);
            return game.View(seat, opponent?.Nickname);
        }
    }

    static RoomStatus StatusOf(Duel game) {
        lock (game) return game.Done ? RoomStatus.Finished : game.Joined ? RoomStatus.Playing : RoomStatus.Waiting;
    }

    static RoomDto Card(string id, Room? row, int seat, UserBrief? opponent, Duel game) =>
        new(id, seat < 0 ? "" : Sides.Name(seat), opponent,
            StatusOf(game).ToString(),
            row?.CreatedAt ?? DateTime.UtcNow, row?.StartedAt, row?.FinishedAt);

    async Task<UserBrief?> BriefAsync(Guid? userId, CancellationToken token) {
        if (userId is not Guid id) return null;
        var user = await unit.Users.ByIdAsync(id, token);
        return new UserBrief(id, user?.Nickname ?? "");
    }

    async Task<T> WithMembership<T>(Func<Task<T>> operation, CancellationToken token) {
        await rooms.Membership.WaitAsync(token);
        try { return await operation(); }
        finally { rooms.Membership.Release(); }
    }

    Duel Require(string id) => rooms.Get(id) ?? throw AppError.NotFound("Комната не найдена");

    static int SeatOrThrow(Duel game, PlayerRef who) {
        var seat = game.SeatOf(who);
        if (seat < 0) throw AppError.Forbidden("Это место не ваше");
        return seat;
    }

    // The board answers a refused move with an HTTP result; the endpoint should not know that
    static AppError Refusal(IResult refused) => refused switch {
        IValueHttpResult { Value: Duel.PurchaseLimitCode } =>
            AppError.Conflict("Достигнут лимит покупок этого типа", Duel.PurchaseLimitCode),
        IStatusCodeHttpResult { StatusCode: StatusCodes.Status409Conflict } => AppError.Conflict("Этот ход недоступен"),
        _ => AppError.BadRequest("Этот ход недоступен")
    };

    static Guid? RivalOf(Room row, Guid me) => row.DefenderId == me ? row.AttackerId : row.DefenderId;

    Task AnnounceAsync(string id, CancellationToken token) =>
        bell.Clients.Group(Bell.RoomGroup(id)).SendAsync(ChangedEvent, token);
}
