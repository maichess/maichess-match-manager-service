Feature: Recording match results on match end
  When a match ends, the result is recorded with user-service for each human
  participant. Bot-vs-bot games record nothing, so bots never affect any
  player's win/loss/draw totals.

  Scenario: A checkmate in a human-vs-human match records a win and a loss
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    And the move validator accepts move "e2e4" resulting in FEN "fen1" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "m1"
    Then 2 match results were recorded
    And a "win" result was recorded for "white-1"
    And a "loss" result was recorded for "black-1"

  Scenario: Black delivering checkmate records black's win and white's loss
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    And the match is at a black-to-move FEN
    And the move validator accepts move "e7e5" resulting in FEN "fen2" with game result "BlackWon"
    When "black-1" makes move "e7e5" on match "m1"
    Then 2 match results were recorded
    And a "loss" result was recorded for "white-1"
    And a "win" result was recorded for "black-1"

  Scenario: A stalemate records a draw for both human players
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    And the move validator accepts move "e2e4" resulting in FEN "fen3" with game result "Stalemate"
    When "white-1" makes move "e2e4" on match "m1"
    Then 2 match results were recorded
    And a "draw" result was recorded for "white-1"
    And a "draw" result was recorded for "black-1"

  Scenario: In a human-vs-bot match only the human result is recorded
    Given an ongoing match "m1" between human "white-1" and bot "stockfish-3"
    And the move validator accepts move "e2e4" resulting in FEN "fen4" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "m1"
    Then 1 match result was recorded
    And a "win" result was recorded for "white-1"

  Scenario: A human-vs-human result rates each player against the other's pre-match rating
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    And user "white-1" has rating 1300 and deviation 90
    And user "black-1" has rating 1700 and deviation 60
    And the move validator accepts move "e2e4" resulting in FEN "fen1" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "m1"
    Then 2 match results were recorded
    And a "win" result was recorded for "white-1" against opponent rating 1700 deviation 60
    And a "loss" result was recorded for "black-1" against opponent rating 1300 deviation 90

  Scenario: A human-vs-bot result rates the human against the bot's elo with a low deviation
    Given an ongoing match "m1" between human "white-1" and bot "stockfish-3"
    And bot "stockfish-3" has elo 2200
    And the move validator accepts move "e2e4" resulting in FEN "fen4" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "m1"
    Then 1 match result was recorded
    And a "win" result was recorded for "white-1" against opponent rating 2200 deviation 50

  Scenario: Resigning records a loss for the resigner and a win for the opponent
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    When "white-1" resigns from match "m1"
    Then 2 match results were recorded
    And a "loss" result was recorded for "white-1"
    And a "win" result was recorded for "black-1"

  Scenario: Accepting a draw records a draw for both players
    Given an ongoing match "m1" between human "white-1" and human "black-1"
    And "white-1" has a pending draw offer on match "m1"
    When "black-1" accepts draw on match "m1"
    Then 2 match results were recorded
    And a "draw" result was recorded for "white-1"
    And a "draw" result was recorded for "black-1"

  Scenario: A bot-vs-bot match ending on timeout records nothing
    Given an ongoing match "m1" between bot "bot-a" and bot "bot-b"
    And the active side has timed out on match "m1"
    When timeout enforcement runs for the match
    Then no match results were recorded

  Scenario: A human-vs-bot match ending on timeout records only the human
    Given an ongoing match "m1" between human "white-1" and bot "bot-b"
    And the active side has timed out on match "m1"
    When timeout enforcement runs for the match
    Then 1 match result was recorded
    And a "loss" result was recorded for "white-1"
