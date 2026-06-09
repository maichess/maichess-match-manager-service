using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Google.Protobuf;

namespace MaichessMatchManagerService.Events;

// Confluent Protobuf serde factory for the maichess.events.v1 generated messages
// (OutboundEvent, MatchEvent, MatchCommand, UserEvent, …). This sits next to the
// Avro serde used by KafkaSocketNotifier / MatchCommandConsumer during the
// per-topic migration: nothing is switched to it yet (the producers/consumers
// keep their Avro serde). Task 02 onwards wires it in — consumers dual-read, then
// producers cut over — so the swap stays a one-line change at each call site.
//
// Reuses the existing protobuf tooling: the generated types ship in the same
// Maichess.PlatformProtos package as the gRPC stubs (Google.Protobuf runtime),
// so the only new dependency is Confluent.SchemaRegistry.Serdes.Protobuf.
[ExcludeFromCodeCoverage]
internal static class ProtobufEventSerdes
{
    // Value serializer for a generated proto envelope; pass to
    // ProducerBuilder.SetValueSerializer (it accepts IAsyncSerializer<T>, exactly
    // as the Avro path passes AvroSerializer<GenericRecord>).
    public static IAsyncSerializer<T> Serializer<T>(ISchemaRegistryClient registry)
        where T : class, IMessage<T>, new()
        => new ProtobufSerializer<T>(registry);

    // Sync value deserializer for the existing synchronous consumer loops
    // (mirrors AvroDeserializer<GenericRecord>(registry).AsSyncOverAsync()).
    public static IDeserializer<T> Deserializer<T>()
        where T : class, IMessage<T>, new()
        => new ProtobufDeserializer<T>().AsSyncOverAsync();
}
