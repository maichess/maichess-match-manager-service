Feature: Get Legal Moves
  Legal moves can be retrieved for any match, with optional filtering by source square.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: Getting legal moves for a non-existent match throws MatchNotFoundException
    When any user requests legal moves on match "unknown-id"
    Then a MatchNotFoundException is thrown

  Scenario: Getting legal moves without a filter returns all moves
    Given the move validator returns legal moves "e2e4,e2e3,d2d4"
    When any user requests legal moves on match "match-1"
    Then the legal moves result contains "e2e4"
    And the legal moves result contains "e2e3"
    And the legal moves result contains "d2d4"

  Scenario: Getting legal moves filtered by source square returns only matching moves
    Given the move validator returns legal moves "e2e4,e2e3,d2d4"
    When any user requests legal moves from "e2" on match "match-1"
    Then the legal moves result contains "e2e4"
    And the legal moves result contains "e2e3"
    And the legal moves result does not contain "d2d4"
