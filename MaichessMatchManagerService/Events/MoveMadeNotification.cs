using MaichessMatchManagerService.Entities;

namespace MaichessMatchManagerService.Events;

internal sealed record MoveMadeNotification(
    string Move,
    string ResultingFen,
    int Index,
    PlayerDocument Player,
    long WhiteTimeMs,
    long BlackTimeMs) : MatchNotification;
