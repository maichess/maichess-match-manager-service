using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Abstraction over real-time event delivery. Implemented by SocketNotifier
// (legacy gRPC transport) and KafkaSocketNotifier (socket.outbound.v1 topic).
// The transport is selected at startup via the Socket:Transport setting.
internal interface ISocketBroadcaster
{
    void BroadcastMoveMade(
        MatchDocument match,
        string move,
        string resultingFen,
        int index,
        PlayerDocument mover,
        long whiteTimeMs,
        long blackTimeMs);

    void BroadcastMatchEnded(MatchDocument match, string status, string reason);

    void BroadcastDrawOffered(MatchDocument match, PlayerDocument offerer);

    void BroadcastDrawDeclined(MatchDocument match, PlayerDocument decliner);
}
