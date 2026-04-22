Feature: Resign Match
  A player can concede at any point during an ongoing match.
  The opponent is declared the winner.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: A non-participant cannot resign
    When "outsider" resigns from match "match-1"
    Then a NotParticipantException is thrown

  Scenario: Resigning an already ended match throws MatchAlreadyEndedException
    Given the match has status "black_won"
    When "white-1" resigns from match "match-1"
    Then a MatchAlreadyEndedException is thrown

  Scenario: White resigns and black wins
    When "white-1" resigns from match "match-1"
    Then the match has status "black_won"

  Scenario: Black resigns and white wins
    When "black-1" resigns from match "match-1"
    Then the match has status "white_won"
