Feature: Sync External Match
    External matches are mirrors of games played on an external server.
    SyncExternalMatch updates the state of an existing external match
    without triggering move validation, bot-move scheduling, or rating updates.

    Scenario: Sync moves and clock to an external match
        Given an external match "ext-1" between external "Alice" and external "Bob"
        When the external match "ext-1" is synced with moves "e2e4,e7e5" and fen "fen-after" and status "ongoing"
        Then the match "ext-1" has 2 moves
        And the match "ext-1" current fen is "fen-after"
        And the match "ext-1" status is "ongoing"

    Scenario: Sync sets clock times
        Given an external match "ext-1" between external "Alice" and external "Bob"
        When the external match "ext-1" is synced with white_time_ms 250000 and black_time_ms 280000
        Then the match "ext-1" has white_time_ms 250000
        And the match "ext-1" has black_time_ms 280000

    Scenario: Sync finalizes a finished external match
        Given an external match "ext-1" between external "Alice" and external "Bob"
        When the external match "ext-1" is synced with status "white_won" and finished_at_ms 1700000000000
        Then the match "ext-1" status is "white_won"
        And the match "ext-1" finished_at_ms is 1700000000000

    Scenario: Sync rejects native match
        Given a native match "nat-1" between user "u1" and user "u2"
        When the native match "nat-1" is synced
        Then the sync fails with InvalidOperationException

    Scenario: Sync rejects unknown match
        When an unknown match "missing" is synced
        Then the sync fails with MatchNotFoundException

    Scenario: Create external match sets source and provider
        When an external match is created with provider "tournament-server" and ref "game-42"
        Then the created match source is "external"
        And the created match external provider is "tournament-server"
        And the created match external ref is "game-42"

    Scenario: Create external match does not trigger bot move
        When an external match is created with bot white "bot-a" and external black "Opponent"
        Then no bot move is triggered
