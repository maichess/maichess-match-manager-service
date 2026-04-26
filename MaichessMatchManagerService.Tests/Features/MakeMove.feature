Feature: Make Move
  Submitting a move validates participants, turn order, and move legality
  before updating match state and broadcasting the event.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: Moving on a non-existent match throws MatchNotFoundException
    When "white-1" makes move "e2e4" on match "unknown-id"
    Then a MatchNotFoundException is thrown

  Scenario: Moving on an ended match throws MatchAlreadyEndedException
    Given the match has status "white_won"
    When "white-1" makes move "e2e4" on match "match-1"
    Then a MatchAlreadyEndedException is thrown

  Scenario: A non-participant cannot make a move
    When "outsider" makes move "e2e4" on match "match-1"
    Then a NotParticipantException is thrown

  Scenario: Moving out of turn throws NotYourTurnException
    When "black-1" makes move "e7e5" on match "match-1"
    Then a NotYourTurnException is thrown

  Scenario: An illegal move is rejected
    Given the move validator rejects the move with reason "Piece cannot move there"
    When "white-1" makes move "e2e5" on match "match-1"
    Then an IllegalMoveException is thrown with reason "Piece cannot move there"

  Scenario: A valid move updates match state
    Given the move validator accepts move "e2e4" resulting in FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match current FEN is "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    And the match move list contains "e2e4"
