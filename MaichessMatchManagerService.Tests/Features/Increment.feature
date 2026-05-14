Feature: Time Format Increment
  When a time format defines an increment, the mover's clock is credited the
  increment after they play a move — but only when the move leaves the match
  ongoing.

  Scenario: A 3+2 match credits white's clock with 2000 ms after their move
    Given an ongoing 3+2 match "match-1" between white "white-1" and black "black-1"
    And the white player has 200000 ms remaining and moved 1 seconds ago
    And the move validator accepts move "e2e4" resulting in FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the returned match WhiteTimeMs is at least 200000

  Scenario: A 5+3 match credits black's clock with 3000 ms after their move
    Given an ongoing 5+3 match "match-1" between white "white-1" and black "black-1"
    And the match is at a black-to-move FEN
    And the black player has 200000 ms remaining and moved 1 seconds ago
    And the move validator accepts move "e7e5" resulting in FEN "fenX" with game result "None"
    When "black-1" makes move "e7e5" on match "match-1"
    Then the returned match BlackTimeMs is at least 200000

  Scenario: A 0 increment match does not change clock except for elapsed time
    Given an ongoing 5+0 match "match-1" between white "white-1" and black "black-1"
    And the white player has 300000 ms remaining and moved 1 seconds ago
    And the move validator accepts move "e2e4" resulting in FEN "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the returned match WhiteTimeMs is less than 300000

  Scenario: A game-ending move does not credit the increment
    Given an ongoing 3+2 match "match-1" between white "white-1" and black "black-1"
    And the white player has 200000 ms remaining and moved 1 seconds ago
    And the move validator accepts move "e2e4" resulting in FEN "fen1" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the returned match WhiteTimeMs is less than 200000
