Feature: Resign Match Edge Cases
  Additional resign scenarios not covered by the main ResignMatch feature.

  Scenario: Resigning from a non-existent match throws MatchNotFoundException
    When "white-1" resigns from match "unknown-id"
    Then a MatchNotFoundException is thrown
