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
