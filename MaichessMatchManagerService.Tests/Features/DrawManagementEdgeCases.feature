Feature: Draw Management Edge Cases
  Additional draw management scenarios covering ended matches, bot opponents, and non-participants.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: Cannot offer draw when match is already ended
    Given the match has status "white_won"
    When "white-1" offers a draw on match "match-1"
    Then a MatchAlreadyEndedException is thrown

  Scenario: Cannot offer draw when opponent is a bot
    Given the black player is a bot with BotId "bot-1"
    When "white-1" offers a draw on match "match-1"
    Then a NotParticipantException is thrown

  Scenario: Cannot accept draw when match is already ended
    Given the match has status "black_won"
    And "white-1" has a pending draw offer on match "match-1"
    When "black-1" accepts draw on match "match-1"
    Then a MatchAlreadyEndedException is thrown

  Scenario: Non-participant cannot accept draw
    When "outsider" accepts draw on match "match-1"
    Then a NotParticipantException is thrown

  Scenario: Cannot decline draw when match is already ended
    Given the match has status "black_won"
    And "white-1" has a pending draw offer on match "match-1"
    When "black-1" declines draw on match "match-1"
    Then a MatchAlreadyEndedException is thrown

  Scenario: Non-participant cannot decline draw
    When "outsider" declines draw on match "match-1"
    Then a NotParticipantException is thrown
