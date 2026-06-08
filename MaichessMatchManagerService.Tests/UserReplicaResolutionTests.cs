using Grpc.Core;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Tests.Support;
using NSubstitute;
using Xunit;

namespace MaichessMatchManagerService.Tests;

// Replica-first resolution in MatchService: the Redis user replica (Stage 3, see
// caching-and-read-models.md) serves username + match-end rating enrichment, falling
// back to the hot GetUser RPC only on a cold miss or a field not yet materialised.
public sealed class UserReplicaResolutionTests
{
    private static UserReplicaRecord Record(
        string? username = null,
        double? rating = null,
        double? rd = null) =>
        new(username, rating, rd, null, null, null, null, null, null, null);

    [Fact]
    public async Task ResolveUsername_ReplicaHit_ServesReplicaAndSkipsRpc()
    {
        var ctx = new MatchServiceContext();
        ctx.SetupUserReplica("u1", Record(username: "alice"));

        string username = await ctx.MatchService.ResolveUsernameAsync("u1", CancellationToken.None);

        Assert.Equal("alice", username);
        _ = ctx.UserService.DidNotReceive().GetUserAsync(
            Arg.Any<GetUserRequest>(),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveUsername_ColdMiss_FallsBackToGetUser()
    {
        var ctx = new MatchServiceContext();

        string username = await ctx.MatchService.ResolveUsernameAsync("u2", CancellationToken.None);

        Assert.Equal("rpc-u2", username);
        _ = ctx.UserService.Received(1).GetUserAsync(
            Arg.Is<GetUserRequest>(r => r.UserId == "u2"),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResolveUsername_ReplicaPresentButUsernameBlank_FallsBack(string? blank)
    {
        var ctx = new MatchServiceContext();
        ctx.SetupUserReplica("u3", Record(username: blank, rating: 1400));

        string username = await ctx.MatchService.ResolveUsernameAsync("u3", CancellationToken.None);

        Assert.Equal("rpc-u3", username);
    }

    [Fact]
    public async Task OpponentRating_ReplicaHit_UsesReplicaRatingNotRpc()
    {
        var ctx = new MatchServiceContext();
        var match = MatchServiceContext.BuildHumanMatch("m1", "white", "black");
        match.WhiteTimeMs = 1;
        match.BlackTimeMs = 1;
        match.LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        ctx.SetupMatch(match);
        ctx.SetupOngoingMatches([match]);

        // GetUser would report 1000/100 for white; the replica reports 2222/33 and wins.
        ctx.SetupUserRating("white", 1000, 100);
        ctx.SetupUserReplica("white", Record(rating: 2222, rd: 33));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        // black's opponent is white, so black's recorded opponent rating is the replica's.
        Assert.Contains(
            ctx.RecordedResults,
            r => r.UserId == "black" && r.OpponentRating == 2222 && r.OpponentRd == 33);
    }

    [Fact]
    public async Task OpponentRating_ReplicaMissingRating_FallsBackToGetUser()
    {
        var ctx = new MatchServiceContext();
        var match = MatchServiceContext.BuildHumanMatch("m2", "white", "black");
        match.WhiteTimeMs = 1;
        match.BlackTimeMs = 1;
        match.LastMoveAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        ctx.SetupMatch(match);
        ctx.SetupOngoingMatches([match]);

        // Replica row exists for white but carries no rating yet (only a username from a
        // UserRegistered snapshot) → resolution must defer to GetUser, not rate at zero.
        ctx.SetupUserRating("white", 1777, 44);
        ctx.SetupUserReplica("white", Record(username: "whitey"));

        await ctx.MatchService.EnforceTimeoutsAsync(CancellationToken.None);

        Assert.Contains(
            ctx.RecordedResults,
            r => r.UserId == "black" && r.OpponentRating == 1777 && r.OpponentRd == 44);
    }
}
