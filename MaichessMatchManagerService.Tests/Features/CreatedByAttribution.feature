Feature: Match creation attribution
  CreateMatch stamps created_by (the initiator) and source NATIVE. When the
  caller does not supply created_by it is derived from the human side.

  Scenario: A human-vs-human match attributes to the white human and is native
    When a match is created with white human "white-1" and black human "black-1"
    Then the match created_by user is "white-1"
    And the match source is "native"

  Scenario: A human-vs-bot match attributes to the human
    When a match is created with white human "white-1" and black bot "stockfish-3"
    Then the match created_by user is "white-1"

  Scenario: A bot-vs-human match attributes to the human on the black side
    When a match is created with white bot "stockfish-3" and black human "black-1"
    Then the match created_by user is "black-1"

  Scenario: A bot-vs-bot match has no initiator when none is supplied
    When a match is created with white bot "bot-a" and black bot "bot-b"
    Then the match has no created_by

  Scenario: An explicit initiator is recorded for a bot-vs-bot match
    When a match is created with white bot "bot-a" and black bot "bot-b" started by "starter-1"
    Then the match created_by user is "starter-1"
