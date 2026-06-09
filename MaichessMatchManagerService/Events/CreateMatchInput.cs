using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

// The fields MatchCommandConsumer needs to materialise a match, decoupled from the
// wire encoding. Both the Avro (GenericRecord) and the Protobuf (MatchCommand) read
// arms project onto this record so the consumer applies a single shape.
internal sealed record CreateMatchInput(
    PlayerDocument White,
    PlayerDocument Black,
    TimeFormatDocument TimeFormat,
    PlayerDocument? CreatedBy,
    string? StartFen,
    string Source,
    string ExternalProvider,
    string ExternalRef,
    string? Id);
