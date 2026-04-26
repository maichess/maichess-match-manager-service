using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Maichess.Database.V1;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Data;

[ExcludeFromCodeCoverage]
internal sealed class MatchRepository(Database.DatabaseClient db) : IMatchRepository
{
    private const string Collection = "matches";

    public async Task<MatchDocument> InsertAsync(MatchDocument match, CancellationToken ct)
    {
        InsertResponse response = await db.InsertAsync(
            new InsertRequest { Collection = Collection, Record = ToStruct(match) },
            cancellationToken: ct);
        return FromStruct(response.Record);
    }

    public async Task<MatchDocument?> GetByIdAsync(string id, CancellationToken ct)
    {
        try
        {
            GetResponse response = await db.GetAsync(
                new GetRequest { Collection = Collection, Id = id },
                cancellationToken: ct);
            return FromStruct(response.Record);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task ReplaceAsync(MatchDocument match, CancellationToken ct)
    {
        Struct fields = ToStruct(match);
        fields.Fields.Remove("id");
        await db.UpdateAsync(
            new UpdateRequest { Collection = Collection, Id = match.Id, Fields = fields },
            cancellationToken: ct);
    }

    private static Struct ToStruct(MatchDocument match)
    {
        Struct s = new();
        s.Fields["id"] = Value.ForString(match.Id);
        s.Fields["white_user_id"] = match.White.UserId is not null
            ? Value.ForString(match.White.UserId) : Value.ForNull();
        s.Fields["white_bot_id"] = match.White.BotId is not null
            ? Value.ForString(match.White.BotId) : Value.ForNull();
        s.Fields["black_user_id"] = match.Black.UserId is not null
            ? Value.ForString(match.Black.UserId) : Value.ForNull();
        s.Fields["black_bot_id"] = match.Black.BotId is not null
            ? Value.ForString(match.Black.BotId) : Value.ForNull();
        s.Fields["current_fen"] = Value.ForString(match.CurrentFen);
        s.Fields["status"] = Value.ForString(match.Status);
        s.Fields["time_control"] = Value.ForString(match.TimeControl);
        s.Fields["white_time_ms"] = Value.ForNumber(match.WhiteTimeMs);
        s.Fields["black_time_ms"] = Value.ForNumber(match.BlackTimeMs);
        s.Fields["last_move_at"] = Value.ForString(
            match.LastMoveAt.ToString("O", CultureInfo.InvariantCulture));
        s.Fields["moves"] = Value.ForList(match.Moves.Select(Value.ForString).ToArray());
        s.Fields["fen_history"] = Value.ForList(match.FenHistory.Select(Value.ForString).ToArray());
        s.Fields["position_history"] = Value.ForList(match.PositionHistory.Select(Value.ForString).ToArray());
        s.Fields["pending_draw_offerer_user_id"] = match.PendingDrawOffererUserId is not null
            ? Value.ForString(match.PendingDrawOffererUserId) : Value.ForNull();
        return s;
    }

    private static MatchDocument FromStruct(Struct s)
    {
        return new MatchDocument
        {
            Id = s.Fields["id"].StringValue,
            White = new PlayerDocument
            {
                UserId = StringOrNull(s, "white_user_id"),
                BotId = StringOrNull(s, "white_bot_id"),
            },
            Black = new PlayerDocument
            {
                UserId = StringOrNull(s, "black_user_id"),
                BotId = StringOrNull(s, "black_bot_id"),
            },
            CurrentFen = s.Fields["current_fen"].StringValue,
            Status = s.Fields["status"].StringValue,
            TimeControl = s.Fields["time_control"].StringValue,
            WhiteTimeMs = (long)s.Fields["white_time_ms"].NumberValue,
            BlackTimeMs = (long)s.Fields["black_time_ms"].NumberValue,
            LastMoveAt = DateTimeOffset.Parse(
                s.Fields["last_move_at"].StringValue, CultureInfo.InvariantCulture),
            Moves = [.. s.Fields["moves"].ListValue.Values.Select(v => v.StringValue)],
            FenHistory = [.. s.Fields["fen_history"].ListValue.Values.Select(v => v.StringValue)],
            PositionHistory = s.Fields.TryGetValue("position_history", out Value? ph)
                ? [.. ph.ListValue.Values.Select(v => v.StringValue)]
                : [],
            PendingDrawOffererUserId = StringOrNull(s, "pending_draw_offerer_user_id"),
        };
    }

    private static string? StringOrNull(Struct s, string key) =>
        s.Fields.TryGetValue(key, out Value? v) && v.KindCase == Value.KindOneofCase.StringValue
            ? v.StringValue
            : null;
}
