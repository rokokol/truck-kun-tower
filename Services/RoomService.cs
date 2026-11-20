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

    public async Task<DuelSession> CreateAsync(PlayerRef who, int side, CancellationToken token = default) {
        var id = Rooms.NewId();
        var game = new Duel();
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
            await unit.SaveAsync(token);
        } else {
            // A guest room is a link and a board: nothing to remember once the tab is closed
            log.LogDebug("Room {Room} is a guest room and stays out of the database", id);
        }

        var opponent = await BriefAsync(game.UserAt(1 - side), token);
        return new DuelSession(id, Sides.Name(side), seat.Token, Card(id, row, side, opponent, game));
    }

    // Coming back to a room you already sit in is a reload, not a second player
    public async Task<DuelSession> JoinAsync(PlayerRef who, string id, int? side = null, CancellationToken token = default) {
        var game = Require(id);
        Duel.Seat seat;
        int mine;
        lock (game) {
            mine = game.SeatOf(who);
            if (mine >= 0) {
                seat = game.SeatAt(mine)!;
            } else {
                // No side asked for means "wherever there is room", so an invite link needs no query
                var wanted = side ?? (game.SeatAt(Duel.Defender) is null ? Duel.Defender : Duel.Attacker);
                if (!game.TryTake(who, wanted, out seat)) throw AppError.Conflict("Место занято");
                mine = wanted;
            }
        }

        var row = await FollowAsync(id, game, who, token);
        await AnnounceAsync(id, token);
        var opponent = await BriefAsync(game.UserAt(1 - mine), token);
        return new DuelSession(id, Sides.Name(mine), seat.Token, Card(id, row, mine, opponent, game));
    }

    public Task<object> StateAsync(PlayerRef who, string id, CancellationToken token = default) =>
        ViewAsync(Require(id), who, token);

    public async Task<RoomDto> RoomAsync(PlayerRef who, string id, CancellationToken token = default) {
        var game = Require(id);
        int seat;
        lock (game) seat = SeatOrThrow(game, who);
        var row = await unit.Rooms.ByIdAsync(id, token);
        var opponent = await BriefAsync(game.UserAt(1 - seat), token);
        return Card(id, row, seat, opponent, game);
    }

    public async Task<object> MoveAsync(PlayerRef who, string id, Move move, CancellationToken token = default) {
        var game = Require(id);
        int seat;
        IResult? refused;
        Duel.ResultWrite? finished;
        lock (game) {
            seat = SeatOrThrow(game, who);
            refused = game.Apply(seat, move);
            // A rematch clears the board without a result, so the result is asked for either way
            finished = game.TakeResult();
        }
        if (refused is not null) throw Refusal(refused);
        if (finished is not null) await Storage.SaveAsync(scopes, log, id, finished);
        await AnnounceAsync(id, token);
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

    public async Task CancelAsync(PlayerRef who, string id, CancellationToken token = default) {
        var game = Require(id);
        lock (game) SeatOrThrow(game, who);
        // The board leaves first: a second cancel finds no room and says so instead of half-closing it
        if (!rooms.Remove(id, out _)) throw AppError.NotFound("Комната не найдена");

        var row = await unit.Rooms.ByIdAsync(id, token);
        if (row is null) log.LogDebug("Room {Room} was a guest room, so nothing was cancelled in the database", id);
        else {
            row.Status = RoomStatus.Cancelled;
            row.FinishedAt = DateTime.UtcNow;
            await unit.SaveAsync(token);
        }
        await AnnounceAsync(id, token);
    }

    // "My duels" outlives the boards: a live one answers with its own status, the rest with the row's
    public async Task<List<RoomDto>> MineAsync(Guid me, CancellationToken token = default) {
        var rows = await unit.Rooms.ForUserAsync(me, token: token);
        var rivals = rows.Select(row => RivalOf(row, me)).OfType<Guid>().Distinct().ToList();
        var names = rivals.Count == 0
            ? new Dictionary<Guid, string>()
            : (await unit.Users.ByIdsAsync(rivals, token)).ToDictionary(user => user.Id, user => user.Nickname);

        return rows.Select(row => {
            var side = row.DefenderId == me ? Duel.Defender : row.AttackerId == me ? Duel.Attacker : -1;
            var rival = RivalOf(row, me);
            var status = rooms.Get(row.Id) is { } live
                ? (live.Joined ? nameof(RoomStatus.Playing) : nameof(RoomStatus.Waiting))
                : row.Status.ToString();
            return new RoomDto(row.Id, side < 0 ? "" : Sides.Name(side),
                rival is Guid id ? new UserBrief(id, names.GetValueOrDefault(id, "")) : null,
                status, row.CreatedAt, row.StartedAt, row.FinishedAt);
        }).ToList();
    }

    // The row follows the board: a guest owns no column, and a full pair means the game is on
    async Task<Room?> FollowAsync(string id, Duel game, PlayerRef who, CancellationToken token) {
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
        await unit.SaveAsync(token);
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

    async Task<object> ViewAsync(Duel game, PlayerRef who, CancellationToken token) {
        int seat;
        lock (game) seat = SeatOrThrow(game, who);
        // The nickname comes from the database, so it is read outside the lock; a swap in that
        // window only labels the names of a board nobody else can see yet
        var opponent = await BriefAsync(game.UserAt(1 - seat), token);
        lock (game) return game.View(seat, opponent?.Nickname);
    }

    static RoomDto Card(string id, Room? row, int seat, UserBrief? opponent, Duel game) =>
        new(id, seat < 0 ? "" : Sides.Name(seat), opponent,
            game.Joined ? nameof(RoomStatus.Playing) : nameof(RoomStatus.Waiting),
            row?.CreatedAt ?? DateTime.UtcNow, row?.StartedAt, row?.FinishedAt);

    async Task<UserBrief?> BriefAsync(Guid? userId, CancellationToken token) {
        if (userId is not Guid id) return null;
        var user = await unit.Users.ByIdAsync(id, token);
        return new UserBrief(id, user?.Nickname ?? "");
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
