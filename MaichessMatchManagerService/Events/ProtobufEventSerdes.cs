using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Google.Protobuf;

namespace MaichessMatchManagerService.Events;

// Raw-Protobuf Kafka serdes for the maichess.events.v1 generated messages
// (OutboundEvent, MatchEvent, MatchCommand, …). Kafka task 09 removed the Confluent
// Schema Registry: the wire format is now the bare Protobuf bytes
// (msg.ToByteArray() / Parser.ParseFrom(bytes)) with the schemas owned solely by
// maichess-api-contracts. This matches the Scala engine / move-validator serde, which
// has always used raw bytes — so the C# and Scala sides now interoperate on the shared
// match.events.v1 / match.commands.v1 topics without a registry mediating the encoding.
[ExcludeFromCodeCoverage]
internal static class ProtobufEventSerdes
{
    // Value serializer for a generated proto envelope; pass to
    // ProducerBuilder.SetValueSerializer.
    public static ISerializer<T> Serializer<T>()
        where T : IMessage<T>
        => new RawSerializer<T>();

    // Value deserializer for the synchronous consumer loops.
    public static IDeserializer<T> Deserializer<T>()
        where T : IMessage<T>, new()
        => new RawDeserializer<T>();

    private sealed class RawSerializer<T> : ISerializer<T>
        where T : IMessage<T>
    {
        public byte[] Serialize(T data, SerializationContext context) => data.ToByteArray();
    }

    private sealed class RawDeserializer<T> : IDeserializer<T>
        where T : IMessage<T>, new()
    {
        private static readonly MessageParser<T> Parser = new(() => new T());

        public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context) =>
            isNull ? new T() : Parser.ParseFrom(data);
    }
}
