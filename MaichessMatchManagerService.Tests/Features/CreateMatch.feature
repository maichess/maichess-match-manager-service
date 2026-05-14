Feature: Create Match
  The match manager creates matches and assigns initial clock values based on time control.

  Scenario: Creating a blitz match sets 300000 ms per player
    When a blitz match is created between white "white-1" and black "black-1"
    Then the created match has WhiteTimeMs 300000
    And the created match has BlackTimeMs 300000

  Scenario: Creating a bullet match sets 180000 ms per player
    When a bullet match is created between white "white-1" and black "black-1"
    Then the created match has WhiteTimeMs 180000
    And the created match has BlackTimeMs 180000

  Scenario: Creating a rapid match sets 600000 ms per player
    When a rapid match is created between white "white-1" and black "black-1"
    Then the created match has WhiteTimeMs 600000
    And the created match has BlackTimeMs 600000

  Scenario: Creating a classical match sets 1800000 ms per player
    When a classical match is created between white "white-1" and black "black-1"
    Then the created match has WhiteTimeMs 1800000
    And the created match has BlackTimeMs 1800000

  Scenario: Creating a match with unknown time control defaults to blitz
    When a unknown match is created between white "white-1" and black "black-1"
    Then the created match has WhiteTimeMs 300000

  Scenario: Created match starts with status ongoing and initial FEN
    When a blitz match is created between white "white-1" and black "black-1"
    Then the created match has status "ongoing"
    And the created match FenHistory starts with the initial FEN

  Scenario: Creating a match with a "3+2" format records the increment
    When a match is created between white "white-1" and black "black-1" with format "3+2"
    Then the created match has WhiteTimeMs 180000
    And the created match has time format "3+2"
    And the created match has IncrementMs 2000

  Scenario: Creating a match with the "10+5" format records the rapid category
    When a match is created between white "white-1" and black "black-1" with format "10+5"
    Then the created match has WhiteTimeMs 600000
    And the created match has time format "10+5"
    And the created match has IncrementMs 5000
