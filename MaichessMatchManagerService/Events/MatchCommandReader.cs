using Maichess.Events.V1;
using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// Pure projection of a Protobuf MatchCommand envelope onto CreateMatchInput. Mirrors
// the Avro GenericRecord reader in MatchCommandConsumer field-for-field so the two
// arms stay observably identical during dual-read.
internal static class MatchCommandReader
{
    internal static bool TryReadCreateMatch(MatchCommand envelope, out CreateMatchInput input)
    {
        if (envelope.PayloadCase != MatchCommand.PayloadOneofCase.CreateMatch)
        {
            input = null!;
            return false;
        }

        CreateMatchCommand command = envelope.CreateMatch;
        input = new CreateMatchInput(
            White: ToPlayer(command.White),
            Black: ToPlayer(command.Black),
            TimeFormat: ToTimeFormat(command.TimeFormat),
            CreatedBy: HasIdentity(command.CreatedBy) ? ToPlayer(command.CreatedBy) : null,
            StartFen: string.IsNullOrEmpty(command.StartFen) ? null : command.StartFen,
            Source: command.Source == MatchSource.External ? "external" : "native",
            ExternalProvider: command.ExternalProvider,
            ExternalRef: command.ExternalRef,
            Id: string.IsNullOrEmpty(envelope.AggregateId) ? null : envelope.AggregateId);
        return true;
    }

    private static bool HasIdentity(Player? player) =>
        player is not null && player.IdentityCase != Player.IdentityOneofCase.None;

    private static PlayerDocument ToPlayer(Player? player) => new()
    {
        UserId = player?.IdentityCase == Player.IdentityOneofCase.UserId ? player.UserId : null,
        BotId = player?.IdentityCase == Player.IdentityOneofCase.BotId ? player.BotId : null,
        ExternalName = player?.IdentityCase == Player.IdentityOneofCase.ExternalName ? player.ExternalName : null,
    };

    private static TimeFormatDocument ToTimeFormat(TimeFormat tf) => new()
    {
        Id = tf?.Id ?? string.Empty,
        BaseMs = tf?.BaseMs ?? 0,
        IncrementMs = tf?.IncrementMs ?? 0,
        Category = tf?.Category ?? string.Empty,
    };
}
