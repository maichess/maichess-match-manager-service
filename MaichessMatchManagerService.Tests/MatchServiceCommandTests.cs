using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Services;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// The command side of MatchService (Kafka task 06): each write method loads the live
// read model, builds the match.events.v1 fact via MatchCommands, and produces it. These
// assert the produced events (captured by the substitute IMatchEventProducer) and the
// cold-state and creation paths; the pure intent->event rules live in MatchCommandsTests.
public sealed class MatchServiceCommandTests
{
    private const string WhiteToMoveFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static LiveMatchState Live(
        string status = "ongoing",
        string fen = WhiteToMoveFen,
        long sequence = 3,
        string white = "white",
        string black = "black",
        string? pendingDrawOfferer = null,
        long whiteTimeMs = 180_000,
        long blackTimeMs = 180_000,
        long lastMoveAtMs = 0) =>
        new(
            MatchId: "m1",
            CurrentFen: fen,
            Status: status,
            WhiteTimeMs: whiteTimeMs,
            BlackTimeMs: blackTimeMs,
            MoveIndex: 0,
            LastMoveAtMs: lastMoveAtMs,
            IncrementMs: 0,
            PositionHistory: [],
            White: new PlayerRef(white, null),
            Black: new PlayerRef(black, null),
            Sequence: sequence,
            PendingDrawOffererUserId: pendingDrawOfferer);

    // ── Move / resign / draw command emission ────────────────────────────────

    [Fact]
    public async Task MakeMove_OnTurn_ProducesMoveSubmitted()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live(sequence: 3));

        await ctx.MatchService.MakeMoveAsync("m1", "white", "e2e4", CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchEvent.PayloadOneofCase.MoveSubmitted, ev.PayloadCase);
        Assert.Equal("e2e4", ev.MoveSubmitted.MoveUci);
        Assert.Equal("white", ev.MoveSubmitted.By.UserId);
        Assert.Equal(4, ev.Sequence);
    }

    [Fact]
    public async Task MakeMove_ColdLiveModel_ThrowsMatchNotFoundAndProducesNothing()
    {
        MatchServiceContext ctx = new();

        await Assert.ThrowsAsync<MatchNotFoundException>(
            () => ctx.MatchService.MakeMoveAsync("m1", "white", "e2e4", CancellationToken.None));

        Assert.Empty(ctx.ProducedEvents);
    }

    [Fact]
    public async Task MakeMove_WrongPlayer_ThrowsAndProducesNothing()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live());

        await Assert.ThrowsAsync<NotYourTurnException>(
            () => ctx.MatchService.MakeMoveAsync("m1", "black", "e7e5", CancellationToken.None));

        Assert.Empty(ctx.ProducedEvents);
    }

    [Fact]
    public async Task Resign_ProducesMatchEnded()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live());

        await ctx.MatchService.ResignMatchAsync("m1", "white", CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
        Assert.Equal(EndReason.Resignation, ev.MatchEnded.EndReason);
    }

    [Fact]
    public async Task OfferDraw_ProducesDrawOffered()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live());

        await ctx.MatchService.OfferDrawAsync("m1", "white", CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchEvent.PayloadOneofCase.DrawOffered, ev.PayloadCase);
        Assert.Equal("white", ev.DrawOffered.By.UserId);
    }

    [Fact]
    public async Task AcceptDraw_ProducesDrawnMatchEnded()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live(pendingDrawOfferer: "white"));

        await ctx.MatchService.AcceptDrawAsync("m1", "black", CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.Draw, ev.MatchEnded.Status);
        Assert.Equal(EndReason.DrawAgreement, ev.MatchEnded.EndReason);
    }

    [Fact]
    public async Task DeclineDraw_ProducesDrawDeclined()
    {
        MatchServiceContext ctx = new();
        ctx.SetupLiveState(Live(pendingDrawOfferer: "white"));

        await ctx.MatchService.DeclineDrawAsync("m1", "black", CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchEvent.PayloadOneofCase.DrawDeclined, ev.PayloadCase);
        Assert.Equal("black", ev.DrawDeclined.By.UserId);
    }

    // ── Creation emits MatchCreated ──────────────────────────────────────────

    [Fact]
    public async Task CreateMatch_Native_ProducesMatchCreatedAndReturnsDocWithoutInserting()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        MatchDocument doc = await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { UserId = "alice" },
            new PlayerDocument { BotId = "stockfish-3" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        Assert.Equal("ongoing", doc.Status);
        Assert.Equal("alice", doc.White.UserId);
        await ctx.Repository.DidNotReceive().InsertAsync(Arg.Any<MatchDocument>(), Arg.Any<CancellationToken>());

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchEvent.PayloadOneofCase.MatchCreated, ev.PayloadCase);
        Assert.Equal("match.MatchCreated", ev.EventType);
        Assert.Equal("match-manager-service", ev.Producer);
        Assert.Empty(ev.CausationId);
        Assert.Equal(doc.Id, ev.AggregateId);
        Assert.Equal(0, ev.Sequence);
        Assert.Equal("alice", ev.MatchCreated.White.UserId);
        Assert.Equal("stockfish-3", ev.MatchCreated.Black.BotId);
        // A human side derives the initiator (white).
        Assert.Equal("alice", ev.MatchCreated.CreatedBy.UserId);
        Assert.Equal(MatchSource.Native, ev.MatchCreated.Source);
    }

    [Fact]
    public async Task CreateMatch_WithBot_SnapshotsTheEngineEloIntoTheCreatedFact()
    {
        MatchServiceContext ctx = new();
        ctx.SetupBot("stockfish-3", 1500);
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { UserId = "alice" },
            new PlayerDocument { BotId = "stockfish-3" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.False(ev.MatchCreated.HasWhiteBotElo);
        Assert.Equal(1500, ev.MatchCreated.BlackBotElo);
    }

    [Fact]
    public async Task CreateMatch_BotVsBot_SnapshotsBothElos()
    {
        MatchServiceContext ctx = new();
        ctx.SetupBot("bot-a", 800);
        ctx.SetupBot("bot-b", 2200);
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(800, ev.MatchCreated.WhiteBotElo);
        Assert.Equal(2200, ev.MatchCreated.BlackBotElo);
    }

    [Fact]
    public async Task CreateMatch_UnknownBot_LeavesTheEloUnset()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "ghost-bot" },
            new PlayerDocument { UserId = "alice" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.False(ev.MatchCreated.HasWhiteBotElo);
        Assert.False(ev.MatchCreated.HasBlackBotElo);
    }

    [Fact]
    public async Task CreateMatch_HumanVsHuman_DoesNotQueryTheEngine()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { UserId = "alice" },
            new PlayerDocument { UserId = "bob" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        _ = ctx.Engine.DidNotReceive().ListBotsAsync(
            Arg.Any<Maichess.Engine.V1.ListBotsRequest>(),
            Arg.Any<global::Grpc.Core.Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Bot-roster cache (task 17) ───────────────────────────────────────────

    [Fact]
    public async Task CreateMatch_Bot_FirstCreation_QueriesEngineOnce()
    {
        MatchServiceContext ctx = new();
        ctx.SetupBot("bot-a", 800);
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { UserId = "alice" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        _ = ctx.Engine.Received(1).ListBotsAsync(
            Arg.Any<Maichess.Engine.V1.ListBotsRequest>(),
            Arg.Any<global::Grpc.Core.Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateMatch_Bot_SecondCreation_ServesFromCache_WithoutQueryingEngineAgain()
    {
        MatchServiceContext ctx = new();
        ctx.SetupBot("bot-a", 800);
        ctx.SetupBot("bot-b", 2200);
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            tf, createdBy: null, startFen: null, ct: CancellationToken.None);
        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            tf, createdBy: null, startFen: null, ct: CancellationToken.None);

        // Two bot-vs-bot creations, but the static roster is fetched only once.
        _ = ctx.Engine.Received(1).ListBotsAsync(
            Arg.Any<Maichess.Engine.V1.ListBotsRequest>(),
            Arg.Any<global::Grpc.Core.Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());

        // The cached roster still produces the correct elo snapshot on the second event.
        Assert.Equal(2, ctx.ProducedEvents.Count);
        Assert.Equal(800, ctx.ProducedEvents[1].MatchCreated.WhiteBotElo);
        Assert.Equal(2200, ctx.ProducedEvents[1].MatchCreated.BlackBotElo);
    }

    [Fact]
    public async Task CreateMatch_Bot_AfterCacheExpiry_RefetchesFromEngine()
    {
        MatchServiceContext ctx = new();
        ctx.SetupBot("bot-a", 800);
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { UserId = "alice" },
            tf, createdBy: null, startFen: null, ct: CancellationToken.None);

        // Evicting the entry stands in for the 10-minute TTL elapsing: either way the
        // next creation sees a cache miss and re-queries the engine.
        ctx.MemoryCache.Remove("engine:bots");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { UserId = "alice" },
            tf, createdBy: null, startFen: null, ct: CancellationToken.None);

        _ = ctx.Engine.Received(2).ListBotsAsync(
            Arg.Any<Maichess.Engine.V1.ListBotsRequest>(),
            Arg.Any<global::Grpc.Core.Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateMatch_WithCallerMintedId_UsesItAsAggregateId()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        MatchDocument doc = await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { UserId = "alice" },
            new PlayerDocument { UserId = "bob" },
            tf,
            createdBy: null,
            startFen: null,
            id: "match-xyz",
            ct: CancellationToken.None);

        Assert.Equal("match-xyz", doc.Id);
        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal("match-xyz", ev.AggregateId);
    }

    [Fact]
    public async Task CreateMatch_BotVsBot_OmitsCreatedBy()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { BotId = "bot-b" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal("bot-a", ev.MatchCreated.White.BotId);
        Assert.Null(ev.MatchCreated.CreatedBy);
    }

    [Fact]
    public async Task CreateMatch_BotWhiteHumanBlack_DerivesBlackAsInitiator()
    {
        // White is a bot, so the human black side is the derived initiator: the
        // bot-vs-human branch of DeriveInitiator must fall through to black, not null.
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { BotId = "bot-a" },
            new PlayerDocument { UserId = "bob" },
            tf,
            createdBy: null,
            startFen: null,
            ct: CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal("bob", ev.MatchCreated.CreatedBy.UserId);
    }

    [Fact]
    public async Task CreateMatch_External_InsertsDirectlyAndProducesNothing()
    {
        MatchServiceContext ctx = new();
        TimeFormatDocument tf = MatchServiceContext.TimeFormatForCategoryName("blitz");

        MatchDocument doc = await ctx.MatchService.CreateMatchAsync(
            new PlayerDocument { ExternalName = "WhiteBot" },
            new PlayerDocument { ExternalName = "BlackBot" },
            tf,
            createdBy: new PlayerDocument { UserId = "dave" },
            startFen: null,
            source: "external",
            externalProvider: "tournament-server",
            externalRef: "g-1",
            ct: CancellationToken.None);

        Assert.Equal("external", doc.Source);
        await ctx.Repository.Received(1).InsertAsync(
            Arg.Is<MatchDocument>(m => m.Source == "external"), Arg.Any<CancellationToken>());
        Assert.Empty(ctx.ProducedEvents);
    }

    // ── Timeout enforcement ──────────────────────────────────────────────────

    [Fact]
    public async Task EnforceTimeouts_ExpiredWhiteClock_ProducesSingleTimeoutMatchEnded()
    {
        MatchServiceContext ctx = new();
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(whiteTimeMs: 1_000, lastMoveAtMs: 0));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
        Assert.Equal(EndReason.Timeout, ev.MatchEnded.EndReason);
    }

    [Fact]
    public async Task EnforceTimeouts_BlackToMoveExpired_BlackLoses()
    {
        MatchServiceContext ctx = new();
        const string blackToMove = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(fen: blackToMove, blackTimeMs: 1_000, lastMoveAtMs: 0));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.WhiteWon, ev.MatchEnded.Status);
    }

    [Fact]
    public async Task EnforceTimeouts_MalformedFen_TreatsWhiteAsActive()
    {
        MatchServiceContext ctx = new();
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(fen: "8", whiteTimeMs: 1_000, lastMoveAtMs: 0));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
    }

    [Fact]
    public async Task EnforceTimeouts_NotExpired_ProducesNothing()
    {
        MatchServiceContext ctx = new();
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(whiteTimeMs: 180_000, lastMoveAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        Assert.Empty(ctx.ProducedEvents);
    }

    [Fact]
    public async Task EnforceTimeouts_ColdLiveModel_SkipsMatch()
    {
        MatchServiceContext ctx = new();
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        Assert.Empty(ctx.ProducedEvents);
    }

    [Fact]
    public async Task EnforceTimeouts_LiveModelAlreadyEnded_SkipsMatch()
    {
        MatchServiceContext ctx = new();
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(status: "white_won", whiteTimeMs: 0, lastMoveAtMs: 0));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        Assert.Empty(ctx.ProducedEvents);
    }

    // The clock that is checked must be the *active* side's: with a recent last-move
    // time and one side's clock far above the elapsed time, only the side genuinely on
    // turn flags. These pin down the active-side clock selection and the active-colour
    // parse against mutations that would read the wrong clock (and never time out).

    [Fact]
    public async Task EnforceTimeouts_WhiteToMove_ChecksWhiteClockNotBlack()
    {
        MatchServiceContext ctx = new();
        long recentMoveMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5_000;
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        ctx.SetupLiveState(Live(
            whiteTimeMs: 1_000, blackTimeMs: 10_000_000, lastMoveAtMs: recentMoveMs));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        // ~5s elapsed exceeds white's 1s but not black's huge clock: white flags.
        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.BlackWon, ev.MatchEnded.Status);
    }

    [Fact]
    public async Task EnforceTimeouts_BlackToMoveViaTwoFieldFen_ChecksBlackClockNotWhite()
    {
        MatchServiceContext ctx = new();
        long recentMoveMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5_000;
        MatchDocument doc = MatchServiceContext.BuildHumanMatch("m1", "white", "black", "ongoing");
        ctx.SetupOngoingMatches([doc]);
        // Two-field FEN ("<board> b") exercises the parts.Length >= 2 active-colour parse.
        ctx.SetupLiveState(Live(
            fen: "8 b", whiteTimeMs: 10_000_000, blackTimeMs: 1_000, lastMoveAtMs: recentMoveMs));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        // ~5s elapsed exceeds black's 1s but not white's huge clock: black flags.
        MatchEvent ev = Assert.Single(ctx.ProducedEvents);
        Assert.Equal(MatchStatus.WhiteWon, ev.MatchEnded.Status);
    }
}
