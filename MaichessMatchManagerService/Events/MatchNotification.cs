using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

internal abstract record MatchNotification;

internal sealed record MoveMadeNotification(
    string Move,
    string ResultingFen,
    int Index,
    PlayerDocument Player,
    long WhiteTimeMs,
    long BlackTimeMs) : MatchNotification;

internal sealed record MatchEndedNotification(
    string Status,
    string Reason) : MatchNotification;
