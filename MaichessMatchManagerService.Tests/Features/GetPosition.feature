Feature: Get Position
  Position history is only accessible when a match is analyzable.
  Indices map to FenHistory: 0 is the start, N is after the N-th move.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: Retrieving position from a non-analyzable match throws AnalysisNotPermittedException
    When any user requests position 0 on match "match-1"
    Then an AnalysisNotPermittedException is thrown

  Scenario: Requesting an out-of-range position index throws PositionIndexOutOfRangeException
    Given the match has status "white_won"
    When any user requests position 5 on match "match-1"
    Then a PositionIndexOutOfRangeException is thrown

  Scenario: Requesting a negative position index throws PositionIndexOutOfRangeException
    Given the match has status "white_won"
    When any user requests position -1 on match "match-1"
    Then a PositionIndexOutOfRangeException is thrown

  Scenario: Position 0 returns the initial FEN with no move and is not current
    Given the match has status "white_won" with move "e2e4" producing FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    When any user requests position 0 on match "match-1"
    Then the position FEN is "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
    And the position move is ""
    And the position is not current

  Scenario: Requesting the last position is marked as current
    Given the match has status "white_won" with move "e2e4" producing FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    When any user requests position 1 on match "match-1"
    Then the position FEN is "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    And the position move is "e2e4"
    And the position is current
