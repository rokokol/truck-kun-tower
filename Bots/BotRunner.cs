using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TruckKun.Tower;

// One worker plans turns. A separate loop sends refresh notices to clients
internal sealed class BotRunner(
    Rooms rooms,
    IHubContext<Bell> bell,
    IServiceScopeFactory scopes,
    ILogger<BotRunner> log) : BackgroundService {
    const int MaxPurchases = 64;
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(125);
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    readonly Channel<string> notifications = Channel.CreateBounded<string>(new BoundedChannelOptions(Rooms.SoloLimit) {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    readonly object gate = new();
    readonly Queue<Ticket> waiting = new();
    readonly HashSet<Ticket> queued = [];
    readonly Dictionary<TurnKey, string> served = [];
    int rotation;

    internal readonly record struct Ticket(string RoomId, Guid MatchId, int Wave);
    readonly record struct TurnKey(Guid MatchId, int Wave);
    readonly record struct Board(string Id, long Order, Ticket? Ticket);
    internal readonly record struct TurnResult(Move[] Purchases, bool PurchaseRefused, bool ReadyRefused,
        Duel.ResultWrite? Finished, Exception? PlanningError);

    protected override Task ExecuteAsync(CancellationToken stopping) =>
        Task.WhenAll(RunTurns(stopping), NotifyAsync(stopping));

    async Task RunTurns(CancellationToken stopping) {
        using var timer = new PeriodicTimer(Interval);
        while (!stopping.IsCancellationRequested) {
            try {
                await timer.WaitForNextTickAsync(stopping);
                await RunOnce(stopping);
            } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
                return;
            } catch (Exception error) {
                log.LogError(error, "Bot pass failed");
            }
        }
    }

    internal static double[] Weights(int side) =>
        side == Duel.Defender ? BotModels.Defense : BotModels.Attack;

    // The caller holds lock (game), so one turn reads and writes without another task
    internal static TurnResult Turn(Duel game, int seat,
        Func<Duel.PlanningView, double[], int, Move?>? pick = null) {
        var choose = pick ?? BotBrain.Pick;
        var moves = new List<Move>();
        Exception? planningError = null;
        bool purchaseRefused = false;
        int wave = game.ObservePlanning(seat).Wave;
        for (int step = 0; step < MaxPurchases; step++) {
            Move? move;
            try { move = choose(game.ObservePlanning(seat), Weights(seat), seat); }
            catch (Exception error) { planningError = error; break; }
            if (move is null) break;
            if (game.Apply(seat, move) is not null) {
                purchaseRefused = true;
                break;
            }
            moves.Add(move);
        }
        bool readyRefused = game.Apply(seat, new Move(wave, "ready", null)) is not null;
        return new(moves.ToArray(), purchaseRefused, readyRefused, game.TakeResult(), planningError);
    }

    internal async Task<Ticket[]> RunOnce(CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        var servedTickets = new List<Ticket>();
        foreach (var board in Rotated()) {
            try {
                if (board.Ticket is { } ticket) Offer(ticket);
            } catch (Exception error) {
                log.LogError(error, "Bot scan failed for room {Room}", board.Id);
            }
        }
        while (TryTake(out var ticket)) {
            token.ThrowIfCancellationRequested();
            try {
                if (await ServeAsync(ticket)) servedTickets.Add(ticket);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception error) {
                log.LogError(error, "Bot turn failed for room {Room}", ticket.RoomId);
            }
        }
        Prune();
        return servedTickets.ToArray();
    }

    // Tests queue stale tickets to prove that a removed or replayed board never moves twice
    internal void Queue(Ticket ticket) {
        lock (gate) {
            if (queued.Add(ticket)) waiting.Enqueue(ticket);
        }
    }

    IEnumerable<Board> Rotated() {
        var boards = Snapshot().ToArray();
        int start;
        lock (gate) {
            start = boards.Length == 0 ? 0 : rotation % boards.Length;
            rotation = boards.Length == 0 ? 0 : (start + 1) % boards.Length;
        }
        return boards.Skip(start).Concat(boards.Take(start));
    }

    IEnumerable<Board> Snapshot() {
        var boards = new List<Board>();
        foreach (var (id, game) in rooms.All) {
            try {
                Ticket? ticket = null;
                long order;
                lock (game) {
                    order = game.CreationOrder;
                    if (rooms.Get(id) == game && game.Solo && !game.Done && game.BotToMove &&
                        game.BotSeat is int seat) {
                        var view = game.ObservePlanning(seat);
                        ticket = new(id, game.MatchId, view.Wave);
                    }
                }
                boards.Add(new(id, order, ticket));
            } catch (Exception error) {
                log.LogError(error, "Bot scan failed for room {Room}", id);
            }
        }
        return boards.OrderBy(board => board.Order);
    }

    void Offer(Ticket ticket) {
        lock (gate) {
            if (served.ContainsKey(Key(ticket)) || !queued.Add(ticket)) return;
            waiting.Enqueue(ticket);
        }
    }

    bool TryTake(out Ticket ticket) {
        lock (gate) {
            if (waiting.Count == 0) {
                ticket = default;
                return false;
            }
            ticket = waiting.Dequeue();
            queued.Remove(ticket);
            return true;
        }
    }

    async Task<bool> ServeAsync(Ticket ticket) {
        var game = rooms.Get(ticket.RoomId);
        if (game is null) return false;
        Exception? planningError;
        Duel.ResultWrite? finished = null;
        lock (game) {
            if (rooms.Get(ticket.RoomId) != game || !game.Solo || game.Done ||
                game.BotSeat is not int seat || !game.BotToMove) return false;
            var view = game.ObservePlanning(seat);
            if (game.MatchId != ticket.MatchId || view.Wave != ticket.Wave || IsServed(ticket)) return false;
            var turn = Turn(game, seat);
            if (turn.ReadyRefused) return false;
            MarkServed(ticket);
            finished = turn.Finished;
            planningError = turn.PlanningError;
        }
        if (planningError is not null) log.LogError(planningError, "Bot stopped buying in room {Room}", ticket.RoomId);
        if (finished is not null) await Storage.SaveAsync(scopes, log, ticket.RoomId, finished);
        notifications.Writer.TryWrite(ticket.RoomId);
        return true;
    }

    async Task NotifyAsync(CancellationToken stopping) {
        try {
            while (await notifications.Reader.WaitToReadAsync(stopping)) await RunNotifications(stopping);
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    internal async Task RunNotifications(CancellationToken stopping = default) {
        while (notifications.Reader.TryRead(out var roomId)) {
            stopping.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            deadline.CancelAfter(SendTimeout);
            try {
                await bell.Clients.Group(Bell.RoomGroup(roomId)).SendAsync("changed", deadline.Token);
            } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
                throw;
            } catch (Exception error) {
                log.LogWarning(error, "Bot refresh failed for room {Room}", roomId);
            }
        }
    }

    bool IsServed(Ticket ticket) {
        lock (gate) return served.ContainsKey(Key(ticket));
    }

    void MarkServed(Ticket ticket) {
        lock (gate) served[Key(ticket)] = ticket.RoomId;
    }

    void Prune() {
        KeyValuePair<TurnKey, string>[] entries;
        lock (gate) entries = served.ToArray();
        foreach (var (key, roomId) in entries) {
            try {
                var game = rooms.Get(roomId);
                bool keep = false;
                if (game is not null) lock (game) {
                    keep = rooms.Get(roomId) == game && game.Solo && !game.Done &&
                        game.MatchId == key.MatchId && game.ResolvedWaves + 1 == key.Wave;
                }
                if (!keep) lock (gate) served.Remove(key);
            } catch (Exception error) {
                log.LogError(error, "Bot ticket cleanup failed for room {Room}", roomId);
            }
        }
    }

    static TurnKey Key(Ticket ticket) => new(ticket.MatchId, ticket.Wave);
}
