using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace TruckKun.Tower.Tests;

public sealed class BotRunnerTests {
    readonly record struct BoardInfo(string Id, Duel Game, int HumanSeat, int BotSeat);

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public async Task A_solo_board_gets_a_bot_move_and_a_ready_within_one_run(int botSide) {
        using var stage = new Stage();
        var board = stage.AddSolo(botSide);
        var legal = Legal(board);
        var gold = Gold(board.Game, board.BotSeat);
        var humanGold = Gold(board.Game, board.HumanSeat);

        await stage.RunAndNotify();

        Assert.False(BotPending(board));
        Assert.NotEmpty(legal);
        Assert.True(Gold(board.Game, board.BotSeat) < gold);
        AssertHumanUntouched(board, humanGold);
        Assert.Single(stage.Events);
        VerifyChanged(stage, board.Id, 1);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void A_turn_uses_the_shipped_model_for_the_bots_side(int botSide) {
        using var stage = new Stage();
        var board = stage.AddSolo(botSide);
        double[]? weights = null;
        int? side = null;
        BotRunner.TurnResult report;
        lock (board.Game) {
            report = BotRunner.Turn(board.Game, board.BotSeat, (_, model, seat) => {
                weights = model;
                side = seat;
                return null;
            });
        }

        Assert.Same(botSide == Duel.Defender ? BotModels.Defense : BotModels.Attack, weights);
        Assert.True(side.HasValue);
        Assert.Equal(botSide, side!.Value);
        Assert.False(report.ReadyRefused);
        Assert.False(BotPending(board));
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void A_refused_unaffordable_purchase_never_reaches_the_board(int botSide) {
        using var stage = new Stage();
        var board = stage.AddSolo(botSide);
        var gold = Gold(board.Game, board.BotSeat);
        var humanGold = Gold(board.Game, board.HumanSeat);
        BotRunner.TurnResult report;
        lock (board.Game) {
            // The defender can afford a ranged slime unless some of the budget is already spent
            if (board.BotSeat == Duel.Defender) {
                var spend = board.Game.ObservePlanning(Duel.Defender).Purchases.First(move => move.Action == "bolt");
                Assert.Null(board.Game.Apply(Duel.Defender, spend));
            }
            report = BotRunner.Turn(board.Game, board.BotSeat, (view, _, _) => {
                Assert.NotEmpty(view.Purchases);
                Assert.True(Duel.PriceOf("ranged") > view.Gold, $"ranged {Duel.PriceOf("ranged")} against {view.Gold}");
                return new Move(view.Wave, "ranged", view.Purchases[0].Cell);
            });
        }

        Assert.True(report.PurchaseRefused);
        Assert.Empty(report.Purchases);
        Assert.False(report.ReadyRefused);
        Assert.False(BotPending(board));
        var spent = board.BotSeat == Duel.Defender ? 5 : 0;
        Assert.Equal(gold - spent, Gold(board.Game, board.BotSeat));
        AssertHumanUntouched(board, humanGold);
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void A_human_side_purchase_cannot_be_applied_by_the_bot(int botSide) {
        using var stage = new Stage();
        var board = stage.AddSolo(botSide);
        Move[] humanLegal;
        lock (board.Game) humanLegal = board.Game.ObservePlanning(board.HumanSeat).Purchases;
        Assert.NotEmpty(humanLegal);
        var candidate = humanLegal[0];
        var humanGold = Gold(board.Game, board.HumanSeat);
        BotRunner.TurnResult report;
        lock (board.Game) {
            report = BotRunner.Turn(board.Game, board.BotSeat,
                (view, _, _) => new Move(view.Wave, candidate.Action, candidate.Cell));
        }

        Assert.True(report.PurchaseRefused);
        Assert.Empty(report.Purchases);
        Assert.False(report.ReadyRefused);
        Assert.False(BotPending(board));
        AssertHumanUntouched(board, humanGold);
    }

    [Theory]
    [InlineData(Duel.Defender)]
    [InlineData(Duel.Attacker)]
    public void The_bot_buys_only_affordable_legal_moves_on_its_own_side(int botSide) {
        using var stage = new Stage();
        var board = stage.AddSolo(botSide);
        var legal = Legal(board);
        var wave = Wave(board);
        var gold = Gold(board.Game, board.BotSeat);
        var humanGold = Gold(board.Game, board.HumanSeat);
        BotRunner.TurnResult report;
        lock (board.Game) report = BotRunner.Turn(board.Game, board.BotSeat);

        Assert.False(report.PurchaseRefused);
        Assert.False(report.ReadyRefused);
        Assert.NotEmpty(report.Purchases);
        int budget = gold;
        foreach (var move in report.Purchases) {
            Assert.Equal(wave, move.Wave);
            Assert.Contains(move, legal);
            var price = Duel.PriceOf(move.Action);
            Assert.True(price.HasValue);
            Assert.True(price!.Value <= budget);
            budget -= price.Value;
        }
        Assert.True(budget >= 0);
        Assert.Equal(budget, Gold(board.Game, board.BotSeat));
        AssertHumanUntouched(board, humanGold);
        Assert.False(BotPending(board));
    }

    [Fact]
    public async Task One_run_serves_every_pending_board() {
        using var stage = new Stage();
        var boards = Enumerable.Range(0, 3).Select(index => stage.AddSolo(index % 2)).ToArray();

        var served = await stage.RunAndNotify();

        Assert.Equal(boards.Length, served.Length);
        Assert.All(boards, board => Assert.False(BotPending(board)));
        Assert.Equal(boards.Length, stage.Events.Count);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_start_room_rotates_between_runs() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Defender);
        var second = stage.AddSolo(Duel.Defender);
        string firstEvent = $"{Bell.RoomGroup(first.Id)}:changed";
        string secondEvent = $"{Bell.RoomGroup(second.Id)}:changed";

        await stage.RunAndNotify();
        Assert.Equal(new[] {firstEvent, secondEvent}, stage.Events);

        HumanReady(first, Wave(first));
        HumanReady(second, Wave(second));
        await stage.RunAndNotify();

        Assert.Equal(new[] {firstEvent, secondEvent, secondEvent, firstEvent}, stage.Events);
        Assert.False(BotPending(first));
        Assert.False(BotPending(second));
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_finished_solo_match_is_dropped_without_a_database_scope() {
        using var stage = new Stage();
        var board = stage.AddSolo(Duel.Defender);

        await PlayToFinish(stage, board);

        Assert.True(IsDone(board));
        Assert.False(BotPending(board));
        Assert.Null(TakeResult(board));
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_finished_solo_match_announces_a_change() {
        using var stage = new Stage();
        var board = stage.AddSolo(Duel.Defender);

        await PlayToFinish(stage, board);

        Assert.True(IsDone(board));
        Assert.NotEmpty(stage.Events);
        Assert.All(stage.Events, name => Assert.EndsWith(":changed", name));
        VerifyChanged(stage, board.Id, stage.Events.Count);
    }

    [Fact]
    public async Task The_cap_refuses_the_next_solo_board_and_leaves_a_normal_board_open() {
        using var stage = new Stage();
        for (int index = 0; index < Rooms.SoloLimit; index++)
            await stage.Service.CreateAsync(PlayerRef.Nobody, Duel.Defender, practice: true);
        Assert.Equal(Rooms.SoloLimit, stage.Rooms.SoloCount);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            stage.Service.CreateAsync(PlayerRef.Nobody, Duel.Defender, practice: true));

        Assert.Equal("solo_busy", error.Code);
        Assert.Equal(409, error.Status);
        Assert.NotEmpty(error.Message);
        var normal = await stage.Service.CreateAsync(PlayerRef.Nobody, Duel.Attacker);
        Assert.NotNull(stage.Rooms.Get(normal.Id));
        Assert.Equal(Rooms.SoloLimit, stage.Rooms.SoloCount);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public void The_cap_counts_only_live_solo_boards() {
        using var stage = new Stage();
        for (int index = 0; index < Rooms.SoloLimit; index++) AddNormal(stage, $"normal-{index}");
        var solo = stage.AddSolo(Duel.Defender);
        var finished = stage.AddSolo(Duel.Defender);
        FinishByReady(finished);

        Assert.Equal(1, stage.Rooms.SoloCount);
        stage.Rooms.RequireSoloCapacity();
        Assert.True(stage.Rooms.Remove(solo.Id, out _));
        Assert.Equal(0, stage.Rooms.SoloCount);
        stage.Rooms.RequireSoloCapacity();
    }

    [Fact]
    public async Task A_served_match_and_wave_is_not_served_twice() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Attacker);
        var ticket = Ticket(first);
        await stage.RunAndNotify();
        Assert.False(BotPending(first));
        int events = stage.Events.Count;
        int gold = Gold(first.Game, first.BotSeat);

        Unready(first);
        Assert.True(BotPending(first));
        stage.Runner.Queue(ticket);
        var second = stage.AddSolo(Duel.Defender);
        var secondTicket = Ticket(second);

        var served = await stage.RunAndNotify();

        Assert.DoesNotContain(ticket, served);
        Assert.Contains(secondTicket, served);
        Assert.True(BotPending(first));
        Assert.Equal(gold, Gold(first.Game, first.BotSeat));
        Assert.False(BotPending(second));
        Assert.Equal(events + 1, stage.Events.Count);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_stale_ticket_does_not_move_the_next_wave_after_resolve() {
        using var stage = new Stage();
        var board = stage.AddSolo(Duel.Defender);
        var stale = Ticket(board);

        ResolveOneWave(board);
        Assert.True(BotPending(board));
        var current = Ticket(board);
        stage.Runner.Queue(stale);

        var served = await stage.RunAndNotify();

        Assert.DoesNotContain(stale, served);
        Assert.Contains(current, served);
        Assert.Single(stage.Events);
        Assert.False(BotPending(board));
        VerifyChanged(stage, board.Id, 1);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_stale_ticket_does_nothing_after_a_finished_match_and_reset() {
        using var stage = new Stage();
        var board = stage.AddSolo(Duel.Defender);
        var stale = Ticket(board);
        FinishByReady(board);

        stage.Runner.Queue(stale);
        var before = await stage.RunAndNotify();
        Assert.Empty(before);
        Assert.Empty(stage.Events);

        Rematch(board);
        Assert.True(BotPending(board));
        var current = Ticket(board);
        stage.Runner.Queue(stale);

        var served = await stage.RunAndNotify();

        Assert.DoesNotContain(stale, served);
        Assert.Contains(current, served);
        Assert.Single(stage.Events);
        Assert.False(BotPending(board));
        VerifyChanged(stage, board.Id, 1);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_removed_rooms_ticket_does_not_touch_a_new_board() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Defender, "solo");
        var stale = Ticket(first);
        Assert.True(stage.Rooms.Remove("solo", out _));
        var second = stage.AddSolo(Duel.Defender, "solo");
        var current = Ticket(second);
        stage.Runner.Queue(stale);

        var served = await stage.RunAndNotify();

        Assert.DoesNotContain(stale, served);
        Assert.Contains(current, served);
        Assert.Single(stage.Events);
        Assert.False(BotPending(second));
        VerifyChanged(stage, second.Id, 1);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_broken_board_does_not_stop_other_rooms() {
        using var stage = new Stage();
        Assert.True(stage.Rooms.Add("broken", null!));
        var board = stage.AddSolo(Duel.Defender);

        await stage.RunAndNotify();

        Assert.False(BotPending(board));
        Assert.Single(stage.Events);
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failed_notification_does_not_stop_other_rooms() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Defender);
        var second = stage.AddSolo(Duel.Attacker);
        stage.ThrowOn(Bell.RoomGroup(first.Id));

        await stage.RunAndNotify();

        Assert.False(BotPending(first));
        Assert.False(BotPending(second));
        Assert.Equal($"{Bell.RoomGroup(second.Id)}:changed", Assert.Single(stage.Events));
        stage.Scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_hung_hub_send_does_not_delay_the_next_bot_turn() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Defender);
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stage.Proxy(Bell.RoomGroup(first.Id)).Setup(channel => channel.SendCoreAsync(It.IsAny<string>(),
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(() => {
                sending.TrySetResult();
                return release.Task;
            });

        var firstPass = stage.Runner.RunOnce();
        var delivery = Task.Run(async () => { await firstPass; await stage.Runner.RunNotifications(); });
        try {
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(firstPass.IsCompletedSuccessfully);
            var second = stage.AddSolo(Duel.Attacker);
            Assert.Single(await stage.Runner.RunOnce());
            Assert.False(BotPending(second));
            Assert.False(delivery.IsCompleted);
        } finally {
            release.TrySetResult();
            await delivery;
        }
    }

    [Fact]
    public async Task A_slow_send_receives_a_deadline_and_the_next_room_gets_its_notice() {
        using var stage = new Stage();
        var first = stage.AddSolo(Duel.Defender);
        var second = stage.AddSolo(Duel.Attacker);
        CancellationToken firstSend = default;
        stage.Proxy(Bell.RoomGroup(first.Id)).Setup(channel => channel.SendCoreAsync(It.IsAny<string>(),
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns((string _, object?[] _, CancellationToken token) => {
                firstSend = token;
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        using var stop = new CancellationTokenSource();
        Assert.Equal(2, (await stage.Runner.RunOnce()).Length);
        var delivery = stage.Runner.RunNotifications(stop.Token);
        try {
            await delivery.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(firstSend.IsCancellationRequested);
            Assert.Equal($"{Bell.RoomGroup(second.Id)}:changed", Assert.Single(stage.Events));
        } finally {
            stop.Cancel();
            try { await delivery; } catch (OperationCanceledException) { }
        }
    }

    // The fixture builds one worker and records hub events
    sealed class Stage : IDisposable {
        internal static readonly Maze Terrain = new(new Random(7));
        readonly Dictionary<string, Mock<IClientProxy>> proxies = [];
        public Rooms Rooms { get; } = new();
        public Mock<IServiceScopeFactory> Scopes { get; } = new(MockBehavior.Strict);
        public Mock<IHubClients> Clients { get; } = new();
        public Mock<IHubContext<Bell>> Hub { get; } = new();
        public List<string> Events { get; } = [];
        public BotRunner Runner { get; }
        public RoomService Service { get; }

        public Stage() {
            Hub.SetupGet(context => context.Clients).Returns(Clients.Object);
            Clients.Setup(all => all.Group(It.IsAny<string>())).Returns((string group) => Proxy(group).Object);
            Runner = new BotRunner(Rooms, Hub.Object, Scopes.Object, NullLogger<BotRunner>.Instance);
            Service = new RoomService(Rooms, Mock.Of<IUnitOfWork>(), Hub.Object, Scopes.Object,
                NullLogger<RoomService>.Instance);
        }

        public Mock<IClientProxy> Proxy(string group) {
            if (proxies.TryGetValue(group, out var proxy)) return proxy;
            proxy = new Mock<IClientProxy>();
            proxy.Setup(channel => channel.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((method, _, _) => Events.Add($"{group}:{method}"))
                .Returns(Task.CompletedTask);
            proxies.Add(group, proxy);
            return proxy;
        }

        public void ThrowOn(string group) =>
            Proxy(group).Setup(channel => channel.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Hub failed"));

        public BoardInfo AddSolo(int botSide, string? id = null) {
            id ??= Rooms.NewId();
            var game = new Duel(Terrain, 3);
            game.SeatBot(botSide);
            int human = 1 - botSide;
            Assert.True(game.TryTake(PlayerRef.Nobody, human, out _));
            Assert.True(Rooms.Add(id, game));
            return new(id, game, human, botSide);
        }

        public async Task<BotRunner.Ticket[]> RunAndNotify() {
            var tickets = await Runner.RunOnce();
            await Runner.RunNotifications();
            return tickets;
        }

        public void Dispose() => Rooms.Dispose();
    }

    static void AddNormal(Stage stage, string id) {
        var game = new Duel(Stage.Terrain, 3);
        Assert.True(game.TryTake(PlayerRef.Nobody, Duel.Defender, out _));
        Assert.True(game.TryTake(PlayerRef.Nobody, Duel.Attacker, out _));
        Assert.True(stage.Rooms.Add(id, game));
    }

    static void VerifyChanged(Stage stage, string id, int times) =>
        stage.Proxy(Bell.RoomGroup(id)).Verify(channel => channel.SendCoreAsync("changed",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    static Move[] Legal(BoardInfo board) {
        lock (board.Game) return board.Game.ObservePlanning(board.BotSeat).Purchases;
    }

    static int Gold(Duel game, int seat) {
        lock (game) return game.ObservePlanning(seat).Gold;
    }

    static int Wave(BoardInfo board) {
        lock (board.Game) return board.Game.ObservePlanning(board.BotSeat).Wave;
    }

    static bool BotPending(BoardInfo board) {
        lock (board.Game) return board.Game.BotToMove;
    }

    static bool IsDone(BoardInfo board) {
        lock (board.Game) return board.Game.Done;
    }

    static Duel.ResultWrite? TakeResult(BoardInfo board) {
        lock (board.Game) return board.Game.TakeResult();
    }

    static BotRunner.Ticket Ticket(BoardInfo board) {
        lock (board.Game) {
            var view = board.Game.ObservePlanning(board.BotSeat);
            return new(board.Id, board.Game.MatchId, view.Wave);
        }
    }

    static void AssertHumanUntouched(BoardInfo board, int gold) {
        Assert.Equal(gold, Gold(board.Game, board.HumanSeat));
        JsonElement view;
        lock (board.Game) view = JsonSerializer.SerializeToElement(board.Game.View(board.HumanSeat),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(0, view.GetProperty("orders").GetArrayLength());
        Assert.Equal(0, view.GetProperty("towers").GetArrayLength());
        Assert.Equal(0, view.GetProperty("fences").GetArrayLength());
    }

    static void HumanReady(BoardInfo board, int wave) {
        lock (board.Game) Assert.Null(board.Game.Apply(board.HumanSeat, new Move(wave, "ready", null)));
    }

    static void ResolveOneWave(BoardInfo board) {
        lock (board.Game) {
            int wave = board.Game.ObservePlanning(board.BotSeat).Wave;
            Assert.Null(board.Game.Apply(board.BotSeat, new Move(wave, "ready", null)));
            Assert.Null(board.Game.Apply(board.HumanSeat, new Move(wave, "ready", null)));
        }
    }

    static void FinishByReady(BoardInfo board) {
        lock (board.Game) {
            for (int attempt = 0; attempt < 10 && !board.Game.Done; attempt++) {
                int wave = board.Game.ObservePlanning(board.BotSeat).Wave;
                Assert.Null(board.Game.Apply(board.BotSeat, new Move(wave, "ready", null)));
                Assert.Null(board.Game.Apply(board.HumanSeat, new Move(wave, "ready", null)));
            }
        }
        Assert.True(IsDone(board));
    }

    static void Rematch(BoardInfo board) {
        lock (board.Game) Assert.Null(board.Game.Apply(board.BotSeat, new Move(1, "rematch", null)));
    }

    // Reflection restores the private ready flag to replay one wave without a new match id
    static void Unready(BoardInfo board) {
        var field = typeof(Duel).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Duel.ready is missing");
        lock (board.Game) {
            var flags = (bool[])field.GetValue(board.Game)!;
            flags[board.BotSeat] = false;
        }
    }

    static async Task PlayToFinish(Stage stage, BoardInfo board) {
        for (int attempt = 0; attempt < 10 && !IsDone(board); attempt++) {
            HumanReady(board, Wave(board));
            await stage.RunAndNotify();
        }
        Assert.True(IsDone(board));
    }
}
