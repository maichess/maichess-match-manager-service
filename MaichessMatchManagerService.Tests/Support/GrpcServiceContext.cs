using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Grpc;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class GrpcServiceContext
{
    internal MatchServiceContext ServiceContext { get; } = new MatchServiceContext();

    internal MatchesGrpcService Service { get; }

    internal TestServerCallContext CallContext { get; } = TestServerCallContext.Create();

    internal GrpcServiceContext()
    {
        Service = new MatchesGrpcService(ServiceContext.MatchService);
    }

    internal void SetupMatch(MatchDocument match) => ServiceContext.SetupMatch(match);

    internal void SetupMoveValidatorAccepts(string move, string resultingFen) =>
        ServiceContext.SetupMoveValidatorAccepts(move, resultingFen);

    internal void SetupMoveValidatorRejects(string reason) =>
        ServiceContext.SetupMoveValidatorRejects(reason);

    internal void SetupListMatches(IEnumerable<MatchDocument> matches, int total) =>
        ServiceContext.SetupListMatches(matches, total);

    internal void SetupFindForUser(IEnumerable<MatchDocument> matches) =>
        ServiceContext.SetupFindForUser(matches);
}
