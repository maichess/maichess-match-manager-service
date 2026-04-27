using System.Diagnostics.CodeAnalysis;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MaichessMatchManagerService.Entities;

using SocketSvc = Socket.V1.Socket;

namespace MaichessMatchManagerService.Events;

[ExcludeFromCodeCoverage]
internal sealed class SocketNotifier(SocketSvc.SocketClient client, ILogger<SocketNotifier> logger)
{
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

    internal void BroadcastMoveMade(
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
        FireAndForget(match, "move_made", payload);
    }

    internal void BroadcastMatchEnded(MatchDocument match, string status, string reason)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["status"] = Value.ForString(status);
        payload.Fields["reason"] = Value.ForString(reason);
        FireAndForget(match, "match_ended", payload);
    }

    internal void BroadcastDrawOffered(MatchDocument match, PlayerDocument offerer)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["player"] = Value.ForStruct(PlayerStruct(offerer));
        FireAndForget(match, "draw_offered", payload);
    }

    internal void BroadcastDrawDeclined(MatchDocument match, PlayerDocument decliner)
    {
        Struct payload = new();
        payload.Fields["match_id"] = Value.ForString(match.Id);
        payload.Fields["player"] = Value.ForStruct(PlayerStruct(decliner));
        FireAndForget(match, "draw_declined", payload);
    }

    private void FireAndForget(MatchDocument match, string @event, Struct payload) =>
        _ = Task.Run(() => EmitToParticipantsAsync(match, @event, payload));

    private async Task EmitToParticipantsAsync(MatchDocument match, string @event, Struct payload)
    {
        List<Task> tasks = [];
        if (match.White.UserId is not null)
        {
            tasks.Add(EmitAsync(match.White.UserId, @event, payload));
        }

        if (match.Black.UserId is not null)
        {
            tasks.Add(EmitAsync(match.Black.UserId, @event, payload));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task EmitAsync(string userId, string @event, Struct payload)
    {
        try
        {
            await client.EmitEventAsync(new Socket.V1.EmitEventRequest
            {
                UserId = userId,
                Event = @event,
                Payload = payload,
            }).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "Failed to emit socket event {Event} to user {UserId}", @event, userId);
        }
    }
}
