Feature: List Matches
  Listing matches drives the Watch feature: clients fetch a paginated
  index of ongoing games filtered optionally by time-format category.

  Scenario: Listing ongoing matches returns repository results
    Given the repository has 2 ongoing matches for category "blitz"
    When ongoing matches are listed for category "blitz" page 1 size 20
    Then the listed match count is 2
    And the listed total is 2

  Scenario: Listing normalises a zero page to page 1
    Given the repository has 1 ongoing matches for category "blitz"
    When ongoing matches are listed for category "blitz" page 0 size 20
    Then the listed match count is 1

  Scenario: Listing caps page_size at 100
    Given the repository has 0 ongoing matches for category "blitz"
    When ongoing matches are listed for category "blitz" page 1 size 500
    Then the listed match count is 0

  Scenario: Listing without a category returns the repository's full result
    Given the repository has 3 ongoing matches for category "blitz"
    When ongoing matches are listed without category on page 1 size 20
    Then the listed match count is 3
