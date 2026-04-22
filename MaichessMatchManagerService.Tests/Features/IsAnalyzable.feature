Feature: Is Analyzable
  The IsAnalyzable predicate controls whether position history can be accessed.
  Matches against bots are always analyzable (learning tool). Ended matches are
  analyzable for replay. Ongoing human-vs-human matches are not.

  Scenario: Ongoing human vs human match is not analyzable
    Given a match with status "ongoing" between two human players
    Then the match is not analyzable

  Scenario: Ongoing match where white is a bot is analyzable
    Given a match with status "ongoing" where white is a bot
    Then the match is analyzable

  Scenario: Ongoing match where black is a bot is analyzable
    Given a match with status "ongoing" where black is a bot
    Then the match is analyzable

  Scenario: A finished match is analyzable
    Given a match with status "white_won" between two human players
    Then the match is analyzable
