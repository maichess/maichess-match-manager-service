using System.Diagnostics.CodeAnalysis;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MaichessMatchManagerService.Entities;

using SocketSvc = Socket.V1.Socket;

namespace MaichessMatchManagerService.Events;

[ExcludeFromCodeCoverage]
internal sealed class SocketNotifier(SocketSvc.SocketClient client, ILogger<SocketNotifier> logger)
    : ISocketBroadcaster
{
    public void BroadcastMoveMade(
        MatchDocument match,
        string move,
        string resultingFen,
        int index,
        PlayerDocument mover,
        long whiteTimeMs,
        long blackTimeMs)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["move"] = Value.ForString(move);
        payload.Fields["resulting_fen"] = Value.ForString(resultingFen);
        payload.Fields["index"] = Value.ForNumber(index);
        payload.Fields["player"] = Value.ForStruct(PlayerStruct(mover));
        payload.Fields["white_time_ms"] = Value.ForNumber(whiteTimeMs);
        payload.Fields["black_time_ms"] = Value.ForNumber(blackTimeMs);
        FireAndForget(match.Id, "move_made", payload);
    }

    public void BroadcastMatchEnded(MatchDocument match, string status, string reason)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["status"] = Value.ForString(status);
        payload.Fields["reason"] = Value.ForString(reason);
        FireAndForget(match.Id, "match_ended", payload);
    }

    public void BroadcastDrawOffered(MatchDocument match, PlayerDocument offerer)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["player"] = Value.ForStruct(PlayerStruct(offerer));
        FireAndForget(match.Id, "draw_offered", payload);
    }

    public void BroadcastDrawDeclined(MatchDocument match, PlayerDocument decliner)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["player"] = Value.ForStruct(PlayerStruct(decliner));
        FireAndForget(match.Id, "draw_declined", payload);
    }

    private static Struct PlayerStruct(PlayerDocument player)
    {
        Struct s = new();
        if (player.UserId is not null)
        {
            s.Fields["user_id"] = Value.ForString(player.UserId);
        }
        else if (player.BotId is not null)
        {
            s.Fields["bot_id"] = Value.ForString(player.BotId);
        }

        return s;
    }

    private void FireAndForget(string matchId, string @event, Struct payload) =>
        _ = Task.Run(() => BroadcastAsync(matchId, @event, payload));

    private async Task BroadcastAsync(string matchId, string @event, Struct payload)
    {
        try
        {
            await client.BroadcastMatchEventAsync(new Socket.V1.BroadcastMatchEventRequest
            {
                MatchId = matchId,
                Event = @event,
                Payload = payload,
            }).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "Failed to broadcast socket event {Event} for match {MatchId}", @event, matchId);
        }
    }
}
