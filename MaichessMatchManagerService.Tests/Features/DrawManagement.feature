Feature: Draw Management
  Players can negotiate a draw. Only the non-offering player can accept or decline.
  Bots cannot participate in draw negotiations.

  Background:
    Given an ongoing blitz match "match-1" between white "white-1" and black "black-1"

  Scenario: A non-participant cannot offer a draw
    When "outsider" offers a draw on match "match-1"
    Then a NotParticipantException is thrown

  Scenario: Cannot offer draw when one is already pending
    Given "white-1" has a pending draw offer on match "match-1"
    When "black-1" offers a draw on match "match-1"
    Then a DrawOfferAlreadyPendingException is thrown

  Scenario: Cannot accept a draw when no offer is pending
    When "black-1" accepts draw on match "match-1"
    Then a NoDrawOfferPendingException is thrown

  Scenario: The offerer cannot accept their own draw offer
    Given "white-1" has a pending draw offer on match "match-1"
    When "white-1" accepts draw on match "match-1"
    Then a NotDrawRecipientException is thrown

  Scenario: Accepting a draw ends the match as draw
    Given "white-1" has a pending draw offer on match "match-1"
    When "black-1" accepts draw on match "match-1"
    Then the match has status "draw"

  Scenario: Declining a draw clears the pending offer
    Given "white-1" has a pending draw offer on match "match-1"
    When "black-1" declines draw on match "match-1"
    Then no draw offer is pending on match "match-1"

  Scenario: White successfully offers a draw
    When "white-1" offers a draw on match "match-1"
    Then the draw offer is from "white-1" on match "match-1"

  Scenario: Black successfully offers a draw
    When "black-1" offers a draw on match "match-1"
    Then the draw offer is from "black-1" on match "match-1"

  Scenario: White declines a draw offered by black
    Given "black-1" has a pending draw offer on match "match-1"
    When "white-1" declines draw on match "match-1"
    Then no draw offer is pending on match "match-1"
