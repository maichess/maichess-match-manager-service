using Avro.Generic;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Pure projection of an Avro MatchCommand envelope (GenericRecord) onto
// CreateMatchInput — the retained Avro arm of the match.commands.v1 dual-read
// (Kafka task 02). Symmetric with the Protobuf MatchCommandReader; both yield the
// same shape so the consumer applies one code path regardless of wire encoding.
internal static class MatchCommandAvroReader
{
    internal static bool TryReadCreateMatch(GenericRecord envelope, out CreateMatchInput input)
    {
        if (!envelope.TryGetValue("payload", out object? payloadObj) ||
            payloadObj is not GenericRecord command ||
            command.Schema.Name != "CreateMatchCommand")
        {
            input = null!;
            return false;
        }

        string matchId = Str(envelope, "aggregate_id");
        PlayerDocument? createdBy =
            command.TryGetValue("created_by", out object? cb) && cb is GenericRecord cbr ? ReadPlayer(cbr) : null;
        string startFen = Str(command, "start_fen");
        bool external = Enum(command, "source").Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase);
        input = new CreateMatchInput(
            White: ReadPlayer((GenericRecord)command["white"]),
            Black: ReadPlayer((GenericRecord)command["black"]),
            TimeFormat: ReadTimeFormat((GenericRecord)command["time_format"]),
            CreatedBy: createdBy,
            StartFen: string.IsNullOrEmpty(startFen) ? null : startFen,
            Source: external ? "external" : "native",
            ExternalProvider: Str(command, "external_provider"),
            ExternalRef: Str(command, "external_ref"),
            Id: string.IsNullOrEmpty(matchId) ? null : matchId);
        return true;
    }

    private static PlayerDocument ReadPlayer(GenericRecord player) => new()
    {
        UserId = player.TryGetValue("user_id", out object? u) ? u as string : null,
        BotId = player.TryGetValue("bot_id", out object? b) ? b as string : null,
    };

    private static TimeFormatDocument ReadTimeFormat(GenericRecord tf) => new()
    {
        Id = Str(tf, "id"),
        BaseMs = (long)tf["base_ms"],
        IncrementMs = (long)tf["increment_ms"],
        Category = Str(tf, "category"),
    };

    private static string Str(GenericRecord record, string field) =>
        record.TryGetValue(field, out object? v) && v is string s ? s : string.Empty;

    private static string Enum(GenericRecord record, string field) =>
        record.TryGetValue(field, out object? v) && v is GenericEnum e ? e.Value : string.Empty;
}
