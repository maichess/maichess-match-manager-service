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

## Match command side + 202 (Kafka task `06`) — DONE

Switched the match write entrypoint from synchronous gRPC to Kafka commands returning
**202**; the authoritative result arrives over the socket. Build + all **283 tests** green;
all new/changed included code is 100% line+branch (the only remaining coverage gaps are the
four pre-existing baseline files noted under task 05: `MatchesGrpcService`,
`MatchCommandReader`, `MatchCommandAvroReader`, `PlayerDocument`).

### What landed

- **`Kafka/MatchCommands.cs`** — pure command-side decision logic. `(LiveMatchState,
  userId, move?, nowMs, newId)` → the `match.events.v1` `MatchEvent`
  (`SubmitMove`→`MoveSubmitted`, `Resign`/`AcceptDraw`→`MatchEnded`, `OfferDraw`→
  `DrawOffered`, `DeclineDraw`→`DrawDeclined`, `Timeout`→`MatchEnded{TIMEOUT}`); validates
  participant/turn/draw rules, throwing the existing exceptions. `sequence = state.Sequence + 1`.
- **`Events/IMatchEventProducer.cs` + `Events/KafkaMatchEventProducer.cs`** — producer seam
  + live-Kafka glue (idempotent non-transactional produce to `match.events.v1`). Glue is
  `[ExcludeFromCodeCoverage]` + excluded from Stryker.
- **`Kafka/LiveMatchState.cs`** — `PendingDrawOffererUserId` for accept/decline validation.
- **`Services/MatchService.cs`** — `MakeMove`/`Resign`/`OfferDraw`/`AcceptDraw`/`DeclineDraw`
  load the live read model (cold ⇒ `MatchNotFoundException` ⇒ REST 404), build the event via
  `MatchCommands`, and produce it. `CreateMatchAsync` emits `MatchCreated` (native) so the
  projector seeds the read model + inserts the durable doc + kicks the first bot move;
  external matches keep the direct insert. Removed the synchronous validate/bot-move/
  broadcast/`RecordMatchResults`/clock code and the `Bots.BotsClient`/`ILogger` ctor deps.
- **`Kafka/MatchProjector.cs` + `MatchProjection.cs`** — the projector now applies a
  *consumed*, command-originated `MatchEnded`/`DrawOffered`/`DrawDeclined` and emits the
  matching socket push (`match_ended`/`draw_offered`/`draw_declined`); its own self-emitted
  `MatchEnded` is deduped by `sequence <= state.Sequence`, so the push fires once.
  `EndReasonToString` gained `resignation` + `draw_agreement`.
- **`Services/MatchService.EnforceTimeoutsAsync`** — scans ongoing matches, reads the
  authoritative clock from the live read model, and emits exactly one `MatchEnded{TIMEOUT}`
  per flagged match via the producer (no direct broadcast). `TimeoutWatchdog` unchanged.
- **`Rest/MatchesEndpoints.cs`** — `/moves`, `/resign`, `/draw-offer`, `/draw-offer/accept`,
  `DELETE /draw-offer` return **202 Accepted**.
- **`Grpc/MatchesGrpcService.cs`** — removed the `MakeMove`/`ResignMatch` overrides (no
  in-cluster caller; the proto RPCs are removed in task `09`). `CreateMatch` returns the
  in-memory doc built from the request.
- **`Program.cs`** — registers `IMatchEventProducer` → `KafkaMatchEventProducer`.
- **Contracts/docs** — `rest/match-manager.md` `/moves` + `/resign` (+ draws) → 202;
  `event-driven-architecture.md` marks the 202 contract live.
- **Client** — `app/api/matches/[id]/{moves,resign}/route.ts` forward the empty 202 without
  parsing JSON; `lib/hooks/useMatch.ts` keeps the optimistic board and reconciles on the
  `move_made`/`match_ended` socket events (no body read). `npm run lint` + `tsc --noEmit`
  clean for these files (4 pre-existing lint errors in analysis/game-library hooks remain).

### Decisions / known gaps

- **RecordMatchResult gap — closed by kafka `08`:** the event-loop match-end path now drives
  ratings via events. Every `MatchEnded` (projector natural end, resign, draw agreement,
  timeout — all built by `Kafka/MatchEndedFactory`) carries the participants, source, and the
  bot sides' elo snapshotted at creation (`CreateMatchAsync` → `ListBots` →
  `MatchCreated.white/black_bot_elo` → `LiveMatchState`); user-service consumes it and applies
  the Glicko-2/W-L-D update idempotently. The `RecordMatchResult` RPC itself is removed in `09`.
- The command side emits **events** on `match.events.v1` (not the `match.commands.v1`
  `SubmitMove`/`Resign`/… messages), matching the task's "emit the corresponding
  event"/"close the loop" and the projector's existing inputs.
- **`MatchEnded` final clocks/FEN (contracts 0.11.0):** `match_events.proto` `MatchEnded`
  gained `white_time_ms = 9`, `black_time_ms = 10`, `final_fen = 11` (backward-compatible,
  zero/empty on pre-0.11.0 events). `Kafka/MatchEndedFactory` populates them from the
  `LiveMatchState` it already holds at every end path, so downstream consumers — the bot
  arena's tie-breaks (task 18) in particular — no longer need a synchronous `GetMatch` read.

---

## Per-move clock history on the match document (move-times task) — additive, no proto change

The durable match document gains `clock_history`: a list of `{ white_time_ms, black_time_ms }`
snapshots, one entry per applied move (`clock_history[i]` = the clocks **after** `moves[i]`), so
the array runs parallel to `moves` (no starting-position entry; `clock_history.Count ==
moves.Count`). It is materialised by the durable write-through fold (`MatchHistoryProjection`):
seeded empty on `MatchCreated`, appended on each `MoveApplied` from the authoritative
`white_time_ms`/`black_time_ms` the projector already stamps onto the event. `MatchRepository`
round-trips it (each entry a sub-struct). **No proto/event change** — `MoveApplied` already
carries the clocks; this only persists what was already computed. The analysis-service also reads
it straight off the match document via `Database.Get(collection="matches")` to annotate exported
PGNs and the analysis move list.

**REST (additive):** `GET /matches/{id}` (`MatchResponse`) now returns an optional `clock_history`
array (`{ white_time_ms, black_time_ms }` per move, parallel to `moves`) alongside the existing
*current*-clock scalars, so the Watch view can export a PGN with `{[%clk]}` annotations. It is
sourced from the durable document (`GetMatchForReadAsync` overlays only the live fen/clocks/status,
never `moves`/`clock_history`, so the two stay aligned); the live read model itself is unchanged.
No existing field changes type or meaning; documented in `rest/match-manager.md`.

Pre-existing match documents have an absent/empty `clock_history`; this is rebuildable from the
event log (replay the `match-manager-projector` group) but no backfill is performed — old games
simply show no per-move times, and every layer treats empty as "no clock data".

## SearchMatches — Dev "All games" browser (task 07) — pending publish (v0.9.0)

`matches.proto` gains `rpc SearchMatches(SearchMatchesRequest) returns (SearchMatchesResponse)`
(global, cross-user, chronological match browse with participant/initiator/status/source/
time-range filters) and `rest/match-manager.md` documents `GET /matches/search`. Implemented in
`MatchService.SearchMatchesAsync` (filter-building + ordering/paging, fully tested), wired through
`MatchesGrpcService.SearchMatches` and the excluded `MatchesEndpoints` `GET /matches/search`;
`MatchRepository.SearchAsync` is the excluded candidate-set push-down.

- **Architecture fit (post-Kafka):** `SearchMatches` is a **read**, and reads stay synchronous in
  the event-driven design (durable history is materialised to match-db by the projector). It mirrors
  the already-shipped `ListUserMatches`/`ListMatches` and reuses the extracted `OrderAndPage` helper.
  The browse list intentionally **does not overlay the Redis live read model** — rows link into the
  match/Watch viewer, which performs the live overlay. No part of this contradicts the CQRS split.
- **Relationship to maichess-search-service:** complementary, not duplicative. search-service
  `GET /search/matches` is per-user faceted/full-text/position search over the Elasticsearch read
  model with best-effort id labels; this is a cross-user chronological feed with match-manager-
  resolved player labels (usernames + bot names) and first-class initiator attribution. The Dev UI
  cross-links the two.
- **Blocker:** these stubs require the published `Maichess.PlatformProtos` **0.9.0**. All consuming
  `*.csproj` and the two Scala `build.sbt` coordinates are bumped 0.8.0 → 0.9.0. Per the versioning
  handoff, the contracts repo must be committed, tagged `v0.9.0`, and pushed before this service can
  restore/build/verify (Claude's shell cannot restore the freshly published package — 401).

---

## Kafka task 09 — `MakeMove`/`ResignMatch` removed from `matches.proto` → PUBLISH HANDOFF

`matches.proto` drops the `MakeMove` + `ResignMatch` RPCs and their `*Request`/`*Response`
messages (`CreateMatch`, `GetMatch`, `ListMatches`, `ListUserMatches`, `SearchMatches`,
`GetMatchPosition`, `SyncExternalMatch` stay). The move/resign write path is already event-sourced
(REST → command on `match.commands.v1`; the move loop runs on `match.events.v1`), so the
`MatchesGrpcService` no longer overrode these RPCs and **no match-manager code references the deleted
types** — the REST `/moves`/`/resign` handlers and the internal `MatchService` move helpers are
unaffected. The socket fan-out moved off `Socket.BroadcastMatchEvent` to `socket.outbound.v1` (the
legacy `SocketNotifier` gRPC impl + `Socket:Transport` flag are deleted). `buf breaking` reports only
the intended deletions.

**Blocked on the same v0.9.0 publish as SearchMatches above** (shared with engine/socket). The
contracts repo bundles both the SearchMatches addition and these task-09 removals into v0.9.0.
**Post-publish:** the `.csproj` is already bumped to 0.9.0; just rebuild/test (`dotnet test`, 286
tests). No code change expected here — the version bump alone should stay green.
