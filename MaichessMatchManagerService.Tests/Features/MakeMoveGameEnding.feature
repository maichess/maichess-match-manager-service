Feature: Make Move Game Ending
  A move can end the game via checkmate, draw conditions, or time expiry.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: A checkmate move sets the match status to white_won
    Given the move validator accepts move "e2e4" resulting in FEN "fen1" with game result "WhiteWon"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "white_won"

  Scenario: A move resulting in black winning sets the match status to black_won
    Given the match is at a black-to-move FEN
    And the move validator accepts move "e7e5" resulting in FEN "fen2" with game result "BlackWon"
    When "black-1" makes move "e7e5" on match "match-1"
    Then the match has status "black_won"

  Scenario: A stalemate move ends the match as draw
    Given the move validator accepts move "e2e4" resulting in FEN "fen3" with game result "Stalemate"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "draw"

  Scenario: The fifty-move rule ends the match as draw
    Given the move validator accepts move "e2e4" resulting in FEN "fen4" with game result "FiftyMoveRule"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "draw"

  Scenario: Threefold repetition ends the match as draw
    Given the move validator accepts move "e2e4" resulting in FEN "fen5" with game result "ThreefoldRepetition"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "draw"

  Scenario: Insufficient material ends the match as draw
    Given the move validator accepts move "e2e4" resulting in FEN "fen6" with game result "InsufficientMaterial"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "draw"

  Scenario: White timing out ends the match as black_won
    Given the white player has 1 ms remaining and moved 5 seconds ago
    And the move validator accepts move "e2e4" resulting in FEN "fen7" with game result "None"
    When "white-1" makes move "e2e4" on match "match-1"
    Then the match has status "black_won"

  Scenario: Black timing out ends the match as white_won
    Given the match is at a black-to-move FEN
    And the black player has 1 ms remaining and moved 5 seconds ago
    And the move validator accepts move "e7e5" resulting in FEN "fen9" with game result "None"
    When "black-1" makes move "e7e5" on match "match-1"
    Then the match has status "white_won"

  Scenario: Black making a valid move decrements black clock
    Given the match is at a black-to-move FEN
    And the move validator accepts move "e7e5" resulting in FEN "fen8" with game result "None"
    When "black-1" makes move "e7e5" on match "match-1"
    Then the returned match BlackTimeMs is less than 300000
