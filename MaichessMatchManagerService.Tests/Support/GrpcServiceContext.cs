using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Grpc;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class GrpcServiceContext
{
    internal MatchServiceContext ServiceContext { get; } = new MatchServiceContext();

    internal MatchesGrpcService Service { get; }

    internal TestServerCallContext CallContext { get; } = TestServerCallContext.Create();

    internal MatchEventBroadcaster Broadcaster => ServiceContext.Broadcaster;

    internal GrpcServiceContext()
    {
        Service = new MatchesGrpcService(ServiceContext.MatchService, ServiceContext.Broadcaster);
    }

    internal void SetupMatch(MatchDocument match) => ServiceContext.SetupMatch(match);

    internal void SetupMoveValidatorAccepts(string move, string resultingFen) =>
        ServiceContext.SetupMoveValidatorAccepts(move, resultingFen);

    internal void SetupMoveValidatorRejects(string reason) =>
        ServiceContext.SetupMoveValidatorRejects(reason);
}
