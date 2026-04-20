# Contract Notes

## Pending package release: Maichess.PlatformProtos v0.2.0

**Blocker:** The `.csproj` references `Maichess.PlatformProtos` v0.2.0, which includes `int32 index = 6` in the `MoveMadeEvent` proto message. This field was added to align the gRPC contract with the REST SSE contract.

**Change needed:** Tag and publish `Maichess.PlatformProtos` v0.2.0 from the `maichess-api-contracts` repository after reviewing the proto change. The service will not build until this package is available on the GitHub package feed.

**Proto change:** `protos/match-manager-service/v1/matches.proto` — `MoveMadeEvent` has a new `int32 index = 6` field.

---

## Automatic draws mapped to "draw_agreement" reason

**Issue:** The `move-validator-service` proto (`moves.proto`) defines `GameResult.GAME_RESULT_DRAW` for automatic draw conditions (50-move rule, threefold repetition, insufficient material). However, the REST and gRPC contracts only define the end reason `draw_agreement` for draw outcomes, which semantically implies a mutual agreement rather than an automatic rule-based draw.

**Current behavior:** All `GAME_RESULT_DRAW` outcomes broadcast `reason: "draw_agreement"` in the `match_ended` event. This is technically incorrect for automatic draws.

**Proposed fix:** Add a `draw` or `automatic_draw` reason to the `EndReason` enum in `matches.proto` and to the REST contract's `reason` field documentation. Until then, `draw_agreement` is used as a catch-all for all draw types.

**Files affected:**
- `maichess-api-contracts/protos/match-manager-service/v1/matches.proto` — add `END_REASON_AUTOMATIC_DRAW` to `EndReason`
- `maichess-api-contracts/api-contracts/rest/match-manager.md` — add `automatic_draw` to the `reason` field documentation
- `maichess-api-contracts/protos/move-validator-service/v1/moves.proto` — consider splitting `GAME_RESULT_DRAW` into more specific values
