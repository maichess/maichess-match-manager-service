using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Abstraction over real-time event delivery. Implemented by KafkaSocketNotifier,
// which publishes to the socket.outbound.v1 topic. The legacy gRPC transport
// (SocketNotifier → Socket.BroadcastMatchEvent) was removed in Kafka task 09.
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
