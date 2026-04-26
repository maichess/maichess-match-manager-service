# Match Manager Service

Instantiates chess matches, accepts move submissions, tracks game state, and streams real-time events to clients. Move legality is delegated to Move Validator; bot moves are requested from Engine. Player usernames are resolved from the User service on each request (stateless).

## Contracts

- **REST:** `maichess-api-contracts/api-contracts/rest/match-manager.md`
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
- **FEN history:** Stored as `FenHistory` array in MongoDB alongside `Moves`. `FenHistory[N]` is the board position after the N-th move; `FenHistory[0]` is the starting position. Used by `GET /matches/{id}/positions/{index}`.
- **Bot moves:** Triggered via a fire-and-forget `Task.Run` after the human ply is confirmed. The bot move is delivered to clients via SSE.
- **Username resolution:** Resolved on demand per request via gRPC to User service. No caching.
- **Bot name resolution:** Resolved on demand via `Engine.ListBots` per request. No caching.
- **SSE broadcasting:** `MatchEventBroadcaster` singleton holds a `Channel<MatchNotification>` per active match. REST SSE handler and gRPC `StreamMatch` both subscribe to it.

## Time Controls (initial clock values)

| Control   | Initial ms |
|-----------|-----------|
| bullet    | 180 000   |
| blitz     | 300 000   |
| rapid     | 600 000   |
| classical | 1 800 000 |

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
  - `TriggerBotMoveIfNeeded` + `ProcessBotMoveAsync` (fire-and-forget)
  - Compiler-generated logging partials (`LogBotMoveFailed`, `LogEngineInvalidMove`)
  - All REST DTO record types (`ErrorResponse`, `MatchResponse`, etc.)
- Coverlet is configured in the test `.csproj` to exclude `Program.cs`, `*.g.cs`, and `*.generated.cs`.

## Entity Framework Rules

N/A — this service delegates all persistence to the database service via gRPC.
