using System.Buffers.Binary;

namespace MaichessMatchManagerService.Events;

// Confluent Schema-Registry wire framing shared by the Avro and Protobuf serdes:
//   byte 0      magic byte (0)
//   bytes 1..4  schema id (big-endian int32)
//   …           serde-specific body
// Avro and Protobuf are indistinguishable from the bytes alone — they differ only
// in what the schema id resolves to in the registry. The dual-read consumers read
// the id here, then look its SchemaType up in the registry (cached) to pick the arm.
internal static class ConfluentFraming
{
    internal const byte MagicByte = 0;

    // Returns the big-endian schema id from a Confluent-framed value, or null if the
    // bytes are too short or not magic-byte framed (a malformed/foreign message).
    internal static int? TryReadSchemaId(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && data[0] == MagicByte
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(1, 4))
            : null;
}
