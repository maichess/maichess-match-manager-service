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

## Topics migrated to Protobuf (Kafka task `02`)

`match.commands.v1` and `socket.outbound.v1` now carry **Protobuf**; the `.avsc` files (canonical +
the embedded `Events/*.avsc` copies for these topics) are retired and build/tests pass locally at
`0.6.0` (the 401 above did not recur — the package is in the NuGet cache). `match.events.v1.avsc`
stays as the canonical Avro reference; the projector (task `05`) now produces **Protobuf**
`MatchEvent`s on that topic (no Avro producer for it).

- **Producer → proto:** `KafkaSocketNotifier` emits `OutboundEvent`.
- **Consumer dual-read:** `MatchCommandConsumer` consumes `byte[]`, discriminates Avro vs Protobuf
  on the schema id's registry type (`ConfluentFraming` + cached lookup), and projects each arm onto
  `CreateMatchInput` via `MatchCommandReader` (proto) / `MatchCommandAvroReader` (Avro). Every
  consume path WARN-logs decode failures (resolving the silent-drop root cause of the socket caveat).
- **Socket caveat resolved:** `Socket__Transport: kafka` for match-manager (the prior grpc revert
  was already absent from `maichess-deploy` base values; an explicit `kafka` entry now codifies it).
- **Decision (deviation from the task's literal step (c)):** the Avro **read** arm is *retained*
  (removed in task `09` with the registry) for reversibility; "no Avro on the wire" still holds.
  Serde glue stays `[ExcludeFromCodeCoverage]`; the pure readers/discriminator are unit-tested.

## Match read model + projector (Kafka task `05`) — DONE

Task `05` builds match-manager's projector + the Redis **live match read model** (CQRS read side
for ongoing matches). No contract change (uses `match.events` payloads from task `01`). Build +
full suite are green (**324 tests**; all new included code is 100% line+branch). The four
pre-existing partially-covered files (`MatchesGrpcService`, `MatchCommandReader`,
`MatchCommandAvroReader`, `PlayerDocument`) are unchanged by this task — verified identical in the
baseline.

### What landed

1. **`Kafka/LiveMatchState.cs`** — the live per-match read-model record (fen, status, clocks, move
   index, last-move time, increment, `position_history`, white/black `PlayerRef`, last-applied
   sequence). Gained **`PendingMoveUci`**: `MoveValidated` carries no move, so the projector stashes
   the UCI from the preceding `MoveSubmitted` here to build `MoveApplied`/`move_made`.
   `Kafka/PlayerRef.cs` holds the per-side identity. `[ExcludeFromCodeCoverage]` data records.
2. **`Kafka/MatchProjection.cs`** — the pure fold `match.events.v1` → `LiveMatchState`
   (`Apply` + `Rebuild`). Extended to track the pending move: `MoveSubmitted` stashes it,
   `MoveApplied`/`MoveRejected` clear it. 100% line+branch.
3. **`Kafka/MatchProjector.cs`** — the pure **decision** logic (`Decide(state, event, nowMs, newId)`
   → `ProjectorOutcome` of new state + match-events + socket pushes). `MoveValidated` → clocks
   (decrement active side, `+increment` only when ongoing — math mirrored from `MatchService`, noted
   duplication) → `MoveApplied` (+ `move_made`); terminal `game_result` **or** a flagged clock →
   `MatchEnded` (+ `match_ended`); else a bot to move → `BotMoveRequested{request_id}`.
   `BotMoveCalculated` → `MoveSubmitted`. `MatchCreated` → first `BotMoveRequested` when a bot is to
   move. Envelope: `aggregate_id`/`correlation_id` copied, `causation_id` = consumed `event_id`,
   `sequence = consumed.Sequence + n`, `producer = "match-manager-service"`. Dedupe: a consumed event
   with `sequence <= state.Sequence` is a no-op. **100% line+branch + the unit tests are the Stryker
   target.**
4. **`Kafka/MatchHistoryProjection.cs`** — the pure durable fold `match.events.v1` → `MatchDocument`
   (the write-through: `MatchCreated` builds the doc; `MoveValidated` sets `position_history`;
   `MoveApplied` appends move+fen, advances clocks; `MatchEnded` finalises). 100% line+branch.
5. **`Data/ILiveMatchState.cs` + `Data/RedisLiveMatchState.cs`** — seam + Redis impl
   (`match:live:{id}` JSON blob), impl `[ExcludeFromCodeCoverage]`.
6. **`Events/MatchEventProjectorConsumer.cs`** (`[ExcludeFromCodeCoverage]` background service) —
   consumes `match.events.v1` (proto serde, `GroupId="match-manager-projector"`), runs `MatchProjector`,
   and produces emitted match-events + `socket.outbound.v1` pushes **inside one Kafka transaction**
   with the consumer offset (consume→produce effectively-once, the C# analogue of the Scala
   `TransactionalProducer`). Post-commit it writes the Redis projection and the durable write-through
   (`MatchHistoryProjection` via `IMatchRepository`; on `MatchEnded` refreshes the finished-match
   cache + evicts participant pages, mirroring `OnMatchEndedAsync`).
7. **REST live reads** — `MatchService.GetMatchForReadAsync` overlays the live fields
   (fen/clocks/last-move time/status) onto the durable doc for an ongoing match, falling back to
   match-db when the model is cold. `GET /matches/{id}` uses it. Kept separate from `GetMatchAsync`
   so the internal write path never persists a doc carrying read-model values.
8. **Wiring + exclusions + docs** — `Program.cs` registers `ILiveMatchState` + the projector
   consumer; Stryker `mutate` excludes add the Redis impl + consumer; service `CLAUDE.md` and the
   `caching-and-read-models.md` / `event-driven-architecture.md` read-model sections updated.

### Notes / deferred
- **Clock-math duplication is deliberate.** `MatchProjector` mirrors
  `MatchService.ApplyGameResult`/`ApplyIncrement`/`GetActiveColor` rather than sharing a helper; the
  synchronous `MatchService` move loop is retired when the write entrypoint cuts over to the
  projector (**task `06`**), at which point the duplication goes away.
- **The projector path is dormant on real traffic until `06`.** Nothing produces the genesis
  `MatchCreated`/`MoveSubmitted` onto `match.events.v1` yet, so the live model stays cold and the REST
  overlay falls through to match-db — which is why wiring it now is non-breaking.
- **Side-effect ordering.** The consumer commits the Kafka transaction (emitted events + offset)
  **before** writing Redis + match-db, so those rebuildable side-effects can be *lost* on a crash but
  never *duplicated* (the offset is already past the event, so it is not redelivered). Recovery is a
  full replay (reset the `match-manager-projector` group). The consumer loads live state via
  `ILiveMatchState.GetAsync` and treats a miss as genesis; **rebuild-from-log on a cold partition is
  not yet wired** (the plan's optional `Rebuild` call) — acceptable while the path is dormant, worth
  doing alongside `06`.
- The **202** REST write-side change is **task `06`**, not here.
