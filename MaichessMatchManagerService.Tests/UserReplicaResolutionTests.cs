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

    // Opponent-rating enrichment moved off the synchronous match-end path with the
    // RecordMatchResult retirement (Kafka task 06 removes the call; task 08 re-homes
    // rating updates onto user.events). The replica-first username resolution above is
    // the part that remains in MatchService.
}
