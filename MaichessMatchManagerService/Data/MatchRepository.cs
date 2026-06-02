using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Maichess.Database.V1;
using MaichessMatchManagerService.Entities;
using MaichessMatchManagerService.Services;

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

    public async Task<IReadOnlyList<MatchDocument>> FindOngoingAsync(CancellationToken ct)
    {
        Struct filter = new();
        filter.Fields["status"] = Value.ForString("ongoing");
        ListResponse response = await db.ListAsync(
            new ListRequest { Collection = Collection, Filter = filter },
            cancellationToken: ct);
        return [.. response.Records.Select(FromStruct)];
    }

    public async Task<IReadOnlyList<MatchDocument>> FindForUserAsync(string userId, CancellationToken ct)
    {
        // The generic database filter is equality-only and ANDs its fields, so a
        // "white OR black OR created_by" query is run as three lookups merged by
        // id. The service applies the authoritative membership/status filtering.
        List<MatchDocument> merged = [];
        HashSet<string> seen = [];
        foreach (string field in new[] { "white_user_id", "black_user_id", "created_by_user_id" })
        {
            Struct filter = new();
            filter.Fields[field] = Value.ForString(userId);
            ListResponse response = await db.ListAsync(
                new ListRequest { Collection = Collection, Filter = filter },
                cancellationToken: ct);
            foreach (Struct record in response.Records)
            {
                MatchDocument match = FromStruct(record);
                if (seen.Add(match.Id))
                {
                    merged.Add(match);
                }
            }
        }

        return merged;
    }

    public async Task<(IReadOnlyList<MatchDocument> Matches, int Total)> ListAsync(
        string status,
        string? category,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        Struct filter = new();
        filter.Fields["status"] = Value.ForString(status);
        if (!string.IsNullOrEmpty(category))
        {
            filter.Fields["time_format_category"] = Value.ForString(category);
        }

        int offset = (page - 1) * pageSize;

        ListResponse listResponse = await db.ListAsync(
            new ListRequest
            {
                Collection = Collection,
                Filter = filter,
                Limit = pageSize,
                Offset = offset,
            },
            cancellationToken: ct);

        CountResponse countResponse = await db.CountAsync(
            new CountRequest { Collection = Collection, Filter = filter },
            cancellationToken: ct);

        IReadOnlyList<MatchDocument> matches = [.. listResponse.Records.Select(FromStruct)];
        return (matches, (int)countResponse.Count);
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
        s.Fields["time_format_id"] = Value.ForString(match.TimeFormat.Id);
        s.Fields["time_format_base_ms"] = Value.ForNumber(match.TimeFormat.BaseMs);
        s.Fields["time_format_increment_ms"] = Value.ForNumber(match.TimeFormat.IncrementMs);
        s.Fields["time_format_category"] = Value.ForString(match.TimeFormat.Category);
        s.Fields["white_time_ms"] = Value.ForNumber(match.WhiteTimeMs);
        s.Fields["black_time_ms"] = Value.ForNumber(match.BlackTimeMs);
        s.Fields["last_move_at"] = Value.ForString(
            match.LastMoveAt.ToString("O", CultureInfo.InvariantCulture));
        s.Fields["moves"] = Value.ForList(match.Moves.Select(Value.ForString).ToArray());
        s.Fields["fen_history"] = Value.ForList(match.FenHistory.Select(Value.ForString).ToArray());
        s.Fields["position_history"] = Value.ForList(match.PositionHistory.Select(Value.ForString).ToArray());
        s.Fields["pending_draw_offerer_user_id"] = match.PendingDrawOffererUserId is not null
            ? Value.ForString(match.PendingDrawOffererUserId) : Value.ForNull();
        s.Fields["created_by_user_id"] = match.CreatedBy?.UserId is not null
            ? Value.ForString(match.CreatedBy.UserId) : Value.ForNull();
        s.Fields["created_by_bot_id"] = match.CreatedBy?.BotId is not null
            ? Value.ForString(match.CreatedBy.BotId) : Value.ForNull();
        s.Fields["source"] = Value.ForString(match.Source);
        s.Fields["external_provider"] = Value.ForString(match.ExternalProvider);
        s.Fields["finished_at_ms"] = Value.ForNumber(match.FinishedAtMs);
        return s;
    }

    private static MatchDocument FromStruct(Struct s)
    {
        TimeFormatDocument timeFormat = ReadTimeFormat(s);
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
            TimeFormat = timeFormat,
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
            CreatedBy = ReadCreatedBy(s),
            Source = s.Fields.TryGetValue("source", out Value? src) &&
                src.KindCase == Value.KindOneofCase.StringValue ? src.StringValue : "native",
            ExternalProvider = s.Fields.TryGetValue("external_provider", out Value? ep) &&
                ep.KindCase == Value.KindOneofCase.StringValue ? ep.StringValue : string.Empty,
            FinishedAtMs = s.Fields.TryGetValue("finished_at_ms", out Value? fa) &&
                fa.KindCase == Value.KindOneofCase.NumberValue ? (long)fa.NumberValue : 0,
        };
    }

    private static PlayerDocument? ReadCreatedBy(Struct s)
    {
        string? userId = StringOrNull(s, "created_by_user_id");
        string? botId = StringOrNull(s, "created_by_bot_id");
        return userId is null && botId is null
            ? null
            : new PlayerDocument { UserId = userId, BotId = botId };
    }

    private static TimeFormatDocument ReadTimeFormat(Struct s)
    {
        // Forward-compatible read: prefer the new flat fields; fall back to the
        // legacy `time_control` string for matches written before v0.3.2.
        if (s.Fields.TryGetValue("time_format_id", out Value? idVal) &&
            idVal.KindCase == Value.KindOneofCase.StringValue)
        {
            return new TimeFormatDocument
            {
                Id = idVal.StringValue,
                BaseMs = (long)s.Fields["time_format_base_ms"].NumberValue,
                IncrementMs = (long)s.Fields["time_format_increment_ms"].NumberValue,
                Category = s.Fields["time_format_category"].StringValue,
            };
        }

        string legacyCategory = s.Fields.TryGetValue("time_control", out Value? tcVal) &&
            tcVal.KindCase == Value.KindOneofCase.StringValue
                ? tcVal.StringValue
                : "blitz";

        return TimeFormatRegistry.Resolve(legacyCategory switch
        {
            "bullet" => "3+0",
            "blitz" => "5+0",
            "rapid" => "10+0",
            "classical" => "30+0",
            _ => "5+0",
        });
    }

    private static string? StringOrNull(Struct s, string key) =>
        s.Fields.TryGetValue(key, out Value? v) && v.KindCase == Value.KindOneofCase.StringValue
            ? v.StringValue
            : null;
}
