Feature: Listing a user's past matches
  ListUserMatches returns matches the user played (either colour) or initiated
  (created_by), filtered to ended matches by default and ordered newest first,
  with paging.

  Scenario: Ended matches the user played or started are listed newest first
    Given a finished match "m1" with white "alice" black "bob" finished at 1000
    And a finished match "m2" with white "carol" black "alice" finished at 3000
    And a finished bot-vs-bot match "m3" created by "alice" finished at 2000
    When matches are listed for user "alice" with status "ended" page 1 size 20
    Then the listed match count is 3
    And the listed total is 3
    And the listed match at position 0 is "m2"
    And the listed match at position 1 is "m3"
    And the listed match at position 2 is "m1"

  Scenario: Matches the user is not part of are excluded
    Given a finished match "m1" with white "alice" black "bob" finished at 1000
    And a finished match "mX" between other players
    When matches are listed for user "alice" with status "ended" page 1 size 20
    Then the listed match count is 1
    And the listed match at position 0 is "m1"

  Scenario: Ongoing matches are excluded from the ended list
    Given a finished match "m1" with white "alice" black "bob" finished at 1000
    And a candidate ongoing match "m2" with white "alice" black "bob"
    When matches are listed for user "alice" with status "ended" page 1 size 20
    Then the listed match count is 1
    And the listed match at position 0 is "m1"

  Scenario: The ongoing filter returns only ongoing matches
    Given a finished match "m1" with white "alice" black "bob" finished at 1000
    And a candidate ongoing match "m2" with white "alice" black "bob"
    When matches are listed for user "alice" with status "ongoing" page 1 size 20
    Then the listed match count is 1
    And the listed match at position 0 is "m2"

  Scenario: Paging returns the requested slice and the full total
    Given a finished match "m1" with white "alice" black "bob" finished at 1000
    And a finished match "m2" with white "alice" black "bob" finished at 2000
    And a finished match "m3" with white "alice" black "bob" finished at 3000
    When matches are listed for user "alice" with status "ended" page 2 size 2
    Then the listed match count is 1
    And the listed total is 3
    And the listed match at position 0 is "m1"
