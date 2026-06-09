# Contract Notes

## Event-driven migration (Kafka) — planned

Per [event-driven-architecture.md](../../maichess-knowledge-base/event-driven-architecture.md),
this service moves from a gRPC orchestrator to an **event-sourced command handler + projector**.
Event/command schemas are Avro in `maichess-api-contracts/events/v1/`.

**Becomes:**
- Consumes `match.commands.v1` (SubmitMove, Resign, OfferDraw, …) and `match.events.v1`
  (MoveValidated, MoveRejected, BotMoveCalculated).
- Produces `match.events.v1` (MatchCreated, MoveSubmitted, MoveApplied, BotMoveRequested,
  MatchEnded, …) and `socket.outbound.v1` (move_made, match_ended, …).
- Maintains the live match read model in **Redis**, rebuilt from `match.events.v1` on startup;
  durable history still materialized to match-db via DatabaseService.
- Gains a **timeout timer** component (timeouts are the absence of a move → emit
  `MatchEnded{TIMEOUT}`).

**Contract change — REST:** `POST /matches/{id}/moves` and `/resign` return **202 Accepted**;
the authoritative result arrives over the socket.io connection. Recorded in the ADR.

**Drops (outbound gRPC):** `Socket.BroadcastMatchEvent`/`EmitEvent`, `Engine.GetBestMove`,
`MoveValidator.ValidateMove`/`GetLegalMoves` (move-loop usage).

**Keeps (synchronous):** REST reads (`GetMatch`, `ListMatches`, `legal-moves`),
`DatabaseService` CRUD, `Users.GetUser` for username resolution.

**Phase 3 dependency — blocked:** the planned `CreateMatchCommand` consumer (consuming
`match.commands.v1` and creating the match from a caller-minted id) requires
`DatabaseService.Insert` to honor a supplied id. See
[maichess-database-service `CONTRACT_NOTES.md`](../maichess-database-service/CONTRACT_NOTES.md).
Until then, match creation stays on `Matches.CreateMatch` gRPC.

Move loop / projector not yet implemented in code — Phase 1 added the Kafka socket producer only.

## Protobuf event serde — implemented (Kafka task `01`)

The event/command schemas are now **Protobuf**, not Avro: `maichess-api-contracts/protos/events/v1/`
(`match_commands.proto`, `match_events.proto`, `socket_outbound.proto`, `user_events.proto`, all
package `maichess.events.v1`). They mirror the `events/v1/*.avsc` field-for-field; the `.avsc` files
stay in place until each topic cuts over (task `02`).

Contracts **v0.6.0** is published; `Maichess.PlatformProtos` is pinned at `0.6.0` in
`MaichessMatchManagerService/MaichessMatchManagerService.csproj` (and
`Confluent.SchemaRegistry.Serdes.Protobuf` is referenced). Done:

1. `Events/ProtobufEventSerdes.cs` — `Serializer<T>` / `Deserializer<T>` factory over the Confluent
   Protobuf serde + the generated `Maichess.Events.V1` types, alongside the Avro path. Serde
   plumbing only; **no producer/consumer is switched in task `01`** — the existing `SocketNotifier`
   / consumer seams are untouched here.
2. `MaichessMatchManagerService.Tests/ProtobufEventRoundTripTests.cs` — round-trips the envelope +
   every payload variant on the topics Match Manager handles (socket.outbound, match.commands,
   match.events, user.events).

**Local verify pending (auth handoff):** a fresh agent shell has no `GITHUB_TOKEN`, so
`dotnet restore` cannot pull `Maichess.PlatformProtos@0.6.0` from GitHub Packages (401). Run
`dotnet test ... -p:CollectCoverage=true` where the token is available to confirm.
