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
stays (that topic has no producer yet — task `05`).

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

## Match read model + projector (Kafka task `05`) — IN PROGRESS (pure core landed)

Task `05` builds match-manager's projector + the Redis **live match read model** (CQRS read side
for ongoing matches). It is large; this session landed the self-contained, fully-tested **pure
read-model core** and leaves the I/O + integration layer as a clearly-scoped follow-up. Build +
full suite are green (278 tests; the new fold is 100% line+branch). No contract change (uses
`match.events` payloads from task `01`).

### Done (committed, green)
1. `Kafka/LiveMatchState.cs` — the live per-match read-model record (matchId, current fen, status,
   white/black clocks, move index, last-move time, increment, `position_history` blob, white/black
   `PlayerRef`, last-applied sequence). `[ExcludeFromCodeCoverage]` data record. `Kafka/PlayerRef.cs`
   holds the per-side identity (user / bot / neither) — split out for SA1402 (one type per file).
2. `Kafka/MatchProjection.cs` — the **pure fold** of `match.events.v1` → `LiveMatchState`
   (`Apply(state, event)` + `Rebuild(log)` for replay-from-log on startup). Each durable fact updates
   only the fields it owns: `MatchCreated` → initial state; `MoveValidated` → the opaque
   `position_history` (the only event carrying it); `MoveApplied` → fen/authoritative clocks/index/
   time; `MatchEnded` → terminal status + clears history. Transient payloads and any event before
   `MatchCreated` leave state unchanged/null. Plus `StatusToString(MatchStatus)`. `internal static`
   (no CA1812), referenced only by tests until the consumer lands.
3. `MaichessMatchManagerService.Tests/MatchProjectionTests.cs` — 16 cases; covers every switch arm,
   both null-state guards, status mapping (incl. default), and the player-ref user/bot/external
   branches. `MatchProjection` is at **100% line + branch**.

   *Note: `Confluent.SchemaRegistry.Serdes.Protobuf` + `Maichess.PlatformProtos@0.6.0` + `StackExchange.Redis@2.8.16` are already referenced in the csproj; no new package needed. The 0.6.0 package is in the local NuGet cache, so no 401.*

### Remaining (next session) — precise plan
- **`Kafka/MatchProjector.cs` (pure decision logic, 100% covered + Stryker).** Given current
  `LiveMatchState` + a consumed `MatchEvent` + a `nowMs`, return the events to emit:
  - `MoveValidated` → compute clocks (elapsed = `nowMs - LastMoveAtMs`, decrement active side via
    fen turn, `+increment` only when the result leaves the game ongoing — **reuse the math in
    `MatchService.ApplyGameResult`/`ApplyIncrement`/`GetActiveColor`**; either lift those to a shared
    pure helper or mirror them into the projector and note the duplication) → build `MoveApplied`
    (fen, index+1, mover `Player`, clocks, `applied_at_ms=nowMs`) + socket `move_made`. If
    `game_result` terminal **or** a clock hit 0 → also `MatchEnded` (+ socket `match_ended`); else if
    the side now to move is a bot (`PlayerRef.BotId`) → `BotMoveRequested{request_id=new guid}`.
  - `BotMoveCalculated` → `MoveSubmitted` (the bot's move; `by` = the bot `Player`) → re-enters the
    validator loop. The new state for Redis is `MatchProjection.Apply` over the emitted
    `MoveValidated`/`MoveApplied`/`MatchEnded` so live + rebuild share one transition.
  - Envelope on every emitted event: copy `aggregate_id`/`correlation_id`, `causation_id` = consumed
    `event_id`, `sequence` = `state.Sequence + n`, `producer = "match-manager-service"`.
  - Dedupe on `(aggregate_id, sequence)`: skip a consumed event whose `sequence <= state.Sequence`.
- **`Data/ILiveMatchState.cs` + `Data/RedisLiveMatchState.cs`** (seam + Redis impl, impl
  `[ExcludeFromCodeCoverage]`). Get/Set the projection keyed `match:live:{id}` (JSON blob).
  `StackExchange.Redis` already referenced (`ConnectionStrings__Redis` wired in the chart).
- **`Events/MatchEventProjectorConsumer.cs`** (`[ExcludeFromCodeCoverage]` background service):
  consume `match.events.v1` (proto serde, `GroupId="match-manager-projector"`); on each record load
  state from `ILiveMatchState` (or `Rebuild` on a cold start), run `MatchProjector`, write the new
  state + **produce emitted events inside a Kafka transaction** (consume→produce effectively-once —
  mirror move-validator's `TransactionalProducer` pattern, the projector path the README mandates
  transactions for). Socket `move_made`/`match_ended` go to `socket.outbound.v1` (reuse
  `KafkaSocketNotifier` shape).
- **Durable write-through:** on `MatchCreated`/`MoveApplied`/`MatchEnded`, materialise history into
  match-db via `IMatchRepository`/`DatabaseService`; on `MatchEnded` keep the existing
  finished-match cache refresh + page eviction (`MatchService.OnMatchEndedAsync`).
- **REST live reads:** `GET /matches/{id}` (+ positions) read `ILiveMatchState` for ongoing matches;
  finished matches keep the `IMatchCache`/match-db path (`MatchService.GetMatchAsync`).
- **Wiring + exclusions:** register the consumer + `ILiveMatchState` in `Program.cs`; add the new
  Redis impl + consumer to the Stryker `mutate` excludes (mirror `RedisMatchCache`/
  `UserReplicaConsumer`). Retire the `match.events.v1.avsc` "no producer yet" note above once the
  projector produces.
- **Docs:** update `caching-and-read-models.md` (live match read model is now implemented, not "not
  implemented") and the `event-driven-architecture.md` read-model section if anything firms up.
- The 202 REST write-side change is **task `06`**, not here.
