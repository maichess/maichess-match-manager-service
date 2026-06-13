# Match Manager Service

Instantiates chess matches, accepts move submissions, tracks game state, and streams real-time events to clients. Move legality is delegated to Move Validator; bot moves are requested from Engine. Player usernames are resolved from the User service on each request (stateless).

## Contracts

- **REST:** `maichess-api-contracts/rest/match-manager.md`
- **gRPC server:** `maichess-api-contracts/protos/match-manager-service/v1/matches.proto`
- **gRPC clients:** `protos/move-validator-service/v1/moves.proto`, `protos/engine-service/v1/bots.proto`, `protos/user-service/v1/users.proto`
- **Generated stubs:** `Maichess.PlatformProtos` NuGet package (see `maichess-api-contracts/dotnet/`)

Implement against these contracts exactly. Document any blocker in `CONTRACT_NOTES.md`.

## Stack

- **Runtime:** ASP.NET (net10.0), C#, nullable enabled
- **Database:** match-db database service via `Database.DatabaseClient` gRPC (`Services:DatabaseService`)
- **RPC:** gRPC server + gRPC clients (stubs from `Maichess.PlatformProtos`)
- **Real-time:** Server-Sent Events (SSE) via `System.Threading.Channels`

## Structure

```
MaichessMatchManagerService/
  Entities/        # MongoDB document model
  Data/            # MongoDB repository
  Events/          # SSE/gRPC event broadcaster and notification types
  Services/        # Core match business logic and exceptions
  Grpc/            # gRPC server implementation (Matches service)
  Rest/            # REST endpoint handlers and response models
  Program.cs       # DI wiring, Kestrel config
```

## Key Design Decisions

- **Clock tracking:** `LastMoveAt` stored in MongoDB; elapsed time is subtracted from the active player's clock on each move. Server is the source of truth.
- **Increment:** When a `TimeFormat` defines a non-zero increment, the mover's clock is credited `increment_ms` after each move that leaves the game ongoing. Game-ending moves do not earn the increment.
- **FEN history:** Stored as `FenHistory` array in MongoDB alongside `Moves`. `FenHistory[N]` is the board position after the N-th move; `FenHistory[0]` is the starting position. Used by `GET /matches/{id}/positions/{index}`.
- **Bot moves (event-sourced, Kafka task 06):** the projector requests a bot move by producing `BotMoveRequested` to `match.events.v1` (on `MatchCreated` when a bot is to move, and after each `MoveApplied` that leaves a bot on turn); the engine stream processor replies with `BotMoveCalculated`, which the projector turns into a `MoveSubmitted` that re-enters the validator loop. The old fire-and-forget `Task.Run` bot path is retired.
- **Finished-match cache:** Immutable (ended) data is cached in Redis behind the `IMatchCache` seam (`Data/RedisMatchCache.cs`): `match:{id}` finished-match docs and `matches:user:{userId}:ended:{page}:{pageSize}` `ListUserMatches` pages, no expiry (allkeys-lru only). Maintained event-driven by `OnMatchEndedAsync` at every match-end path (refresh doc + evict white/black/`created_by` pages, canonical ids). Ongoing matches/pages are never cached — that is the live read model's job. Rebuildable from match-db. See `maichess-knowledge-base/caching-and-read-models.md`.
- **User replica (Stage 3):** Username resolution and match-end opponent-rating enrichment read a Redis-materialised user replica (`user:{id}`, hash) instead of the hot `GetUser` RPC, with a `GetUser` fallback for a cold miss or a not-yet-materialised field. The replica is fed by `Kafka/UserReplicaConsumer.cs` (consumes compacted `user.events.v1` from the beginning) through the pure `Kafka/UserReplicaProjection.cs`, behind the `Data/IUserReplica` seam (Redis impl `Data/RedisUserReplica.cs`, `[ExcludeFromCodeCoverage]`). Replica-vs-RPC orchestration lives in `MatchService` (`ResolveUsernameAsync`, `ResolveOpponentRatingAsync`) where it is unit-tested. Rebuildable by replaying the topic (reset the `match-manager-user-replica` consumer group). See `maichess-knowledge-base/caching-and-read-models.md` (Stage 3).
- **Live match read model (CQRS read side):** The projector (`Events/MatchEventProjectorConsumer.cs`) consumes `match.events.v1` and maintains a per-match live projection in Redis (`match:live:{id}`, JSON blob) behind the `Data/ILiveMatchState` seam (Redis impl `Data/RedisLiveMatchState.cs`, `[ExcludeFromCodeCoverage]`). The decision logic is the pure, fully-tested `Kafka/MatchProjector.cs`: `MoveValidated → MoveApplied (+ socket move_made)`, then a terminal result/flagged clock `→ MatchEnded (+ match_ended)` or a bot to move `→ BotMoveRequested`; `BotMoveCalculated → MoveSubmitted` (re-enters the validator loop); `MatchCreated` kicks the first bot move. The fold of events into the live state is `Kafka/MatchProjection.cs`; the durable write-through to match-db is the parallel fold `Kafka/MatchHistoryProjection.cs`. Consume→produce runs in a Kafka transaction (effectively-once); the Redis projection and match-db write-through are rebuildable side-effects (replay the log / reset the `match-manager-projector` group). REST `GET /matches/{id}` overlays the live fields (fen/clocks/last-move time/status) onto the durable doc for ongoing matches via `MatchService.GetMatchForReadAsync`, falling back to match-db when the model is cold. The move loop clock math lives solely in `MatchProjector` now — Kafka task 06 cut the write side over to the projector and retired the synchronous `MatchService` move/clock path. See `maichess-knowledge-base/caching-and-read-models.md`.
- **Bot name resolution:** Resolved on demand via `Engine.ListBots` per request in the REST adapter (`MatchesEndpoints`). No caching there.
- **Bot-roster cache (caching task 17):** The bot-elo snapshot path in `MatchService.CreateMatchAsync` (which needs the engine's bot list to stamp `WhiteBotElo`/`BlackBotElo` onto `MatchCreated`) caches the static roster behind a 10-minute `IMemoryCache` entry (key `engine:bots`) via the private `GetBotListAsync`, dropping the per-bot-match-creation `ListBots` gRPC roundtrip. `builder.Services.AddMemoryCache()` is wired in `Program.cs`.
- **Rating leaderboard (caching task 12, Part B):** A Redis sorted set `leaderboard:rating` (userId → Glicko-2 rating) behind the `Data/ILeaderboard` seam (Redis impl `Data/RedisLeaderboard.cs`, `[ExcludeFromCodeCoverage]`). Fed by the **same Stage 3 rating consumer** (`UserReplicaConsumer`) through the pure `Kafka/LeaderboardProjection` — one source of truth, rebuildable by replaying `user.events.v1`. The read side `Services/LeaderboardService` enriches ZSET entries from the `user:{id}` replica: hides `flagged` players and annotates `provisional` ratings (deviation > 110), with raw 1-based ZSET ranks (`ZREVRANGE` top-N, `ZREVRANK` "your rank"). Served at REST `GET /leaderboard` and `GET /leaderboard/rank/{user_id}` (`Rest/LeaderboardEndpoints.cs`). See `maichess-knowledge-base/knowledge/architecture/caching-and-read-models.md` (Stage 4).
- **Socket broadcasting:** `KafkaSocketNotifier` (the sole `ISocketBroadcaster`) publishes an `OutboundEvent` to the `socket.outbound.v1` Kafka topic; the socket service consumes it and fan-outs to every client subscribed to the `match:<id>` room (participants and Watch spectators alike). The legacy `SocketNotifier` → `Socket.BroadcastMatchEvent` gRPC path was removed in Kafka task 09.

## Time Formats

Match clock rules are described by a `TimeFormat` value object: `{ id, base_ms, increment_ms, category }`. The canonical preset list lives in `Services/TimeFormatRegistry.cs` and is mirrored by Match Maker's `GET /time-formats` endpoint. Documents written by this service store the format as four flat fields (`time_format_id`, `time_format_base_ms`, `time_format_increment_ms`, `time_format_category`) so the equality-only Database filter can paginate matches by category.

## Code Style

- All compiler warnings are errors (`TreatWarningsAsErrors=true`); `CS1591` is exempted.
- `EnableNETAnalyzers`, `AnalysisMode=All`, `EnforceCodeStyleInBuild=true`, StyleCop.Analyzers.
- Prefer direct, readable code. No repository pattern beyond `MatchRepository`; no extra service-layer abstractions.
- Use C# records for DTOs and response models.
- Use sealed classes throughout; no public types unless required by framework.
- Validate inputs at REST/gRPC boundaries. Trust internal data after that.
- No comments unless explaining a non-obvious algorithm or constraint.

## Testing Requirements

- 100% coverage (line, branch, method) on all included code is mandatory. Run `dotnet test MaichessMatchManagerService.Tests/MaichessMatchManagerService.Tests.csproj -p:CollectCoverage=true "-p:Include=[MaichessMatchManagerService]*"` to verify.
- Test framework: Reqnroll BDD (feature files + step definitions) for MatchService business logic; plain xUnit `[Fact]` tests for MatchEventBroadcaster and MatchesGrpcService.
- Write or update tests alongside every code change.
- Excluded from coverage (marked with `[ExcludeFromCodeCoverage]`):
  - `MatchesEndpoints` class (REST adapter layer)
  - `MatchRepository` class (requires MongoDB)
  - `RedisMatchCache`, `RedisUserReplica`, `RedisLiveMatchState`, and `RedisLeaderboard` (require live Redis; `LeaderboardService` is unit-tested against a mocked `ILeaderboard`)
  - `UserReplicaConsumer` (live-Kafka consumer shell; the pure `UserReplicaProjection` it delegates to is unit-tested)
  - `CheatFlagConsumer` (live-Kafka consumer shell folding compacted `cheat.events.v1` into the replica's `flagged` bit; the pure `CheatFlagProjection` it delegates to is unit-tested — `PlayerFlagged`/`PlayerUnflagged` set the bit, the advisory `LiveSuspicionRaised` is ignored by contract)
  - `MatchEventProjectorConsumer` (live-Kafka consume→produce shell; the pure `MatchProjector` + `MatchHistoryProjection` it delegates to are unit-tested)
  - `KafkaMatchEventProducer` (live-Kafka producer shell for the command side; the pure `MatchCommands` it emits are unit-tested)
  - Pure read-model data records (`LiveMatchState`, `PlayerRef`, `ProjectorOutcome`)
  - All REST DTO record types (`ErrorResponse`, `MatchResponse`, etc.)
- Coverlet is configured in the test `.csproj` to exclude `Program.cs`, `*.g.cs`, and `*.generated.cs`.

### Mutation testing

Stryker.NET is wired up as a local dotnet tool. Config lives in
`MaichessMatchManagerService.Tests/stryker-config.json`; the same files
excluded from coverage (REST endpoints, `MatchRepository`, fire-and-forget bot
helpers) are also excluded from mutation. Run via `dotnet tool restore` then
`dotnet stryker` inside the test project directory. See `README.md` for
details. Mutation testing is not required to pass on every change, but use it
when investigating whether tests genuinely exercise behaviour.

## Entity Framework Rules

N/A — this service delegates all persistence to the database service via gRPC.
