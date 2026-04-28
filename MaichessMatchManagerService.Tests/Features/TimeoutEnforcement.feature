Feature: Timeout Enforcement
  EnforceTimeoutsAsync scans ongoing matches and ends any where the active
  player's clock has expired, broadcasting a match_ended event.

  Scenario: No ongoing matches — nothing happens
    Given no ongoing matches exist for timeout enforcement
    When timeout enforcement runs
    Then no matches were saved by enforcement

  Scenario: Active player still has time remaining — match is not ended
    Given timeout enforcement match "m1" with white "w1" and black "b1"
    And enforcement match "m1" white clock is 60000ms and last move was 5 seconds ago
    And the enforcement ongoing list is "m1"
    When timeout enforcement runs
    Then enforcement match "m1" still has status "ongoing"

  Scenario: White times out — match ends as black_won
    Given timeout enforcement match "m1" with white "w1" and black "b1"
    And enforcement match "m1" white clock is 1ms and last move was 5 seconds ago
    And the enforcement ongoing list is "m1"
    When timeout enforcement runs
    Then enforcement match "m1" still has status "black_won"

  Scenario: Black times out — match ends as white_won
    Given timeout enforcement match "m1" with white "w1" and black "b1"
    And enforcement match "m1" is at a black-to-move position
    And enforcement match "m1" black clock is 1ms and last move was 5 seconds ago
    And the enforcement ongoing list is "m1"
    When timeout enforcement runs
    Then enforcement match "m1" still has status "white_won"

  Scenario: Multiple matches — only expired one is ended
    Given timeout enforcement match "m1" with white "w1" and black "b1"
    And enforcement match "m1" white clock is 1ms and last move was 5 seconds ago
    And timeout enforcement match "m2" with white "w2" and black "b2"
    And enforcement match "m2" white clock is 60000ms and last move was 5 seconds ago
    And the enforcement ongoing list is "m1" and "m2"
    When timeout enforcement runs
    Then enforcement match "m1" still has status "black_won"
    And enforcement match "m2" still has status "ongoing"
