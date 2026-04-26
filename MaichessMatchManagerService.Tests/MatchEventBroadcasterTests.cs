using MaichessMatchManagerService.Events;
using Xunit;

namespace MaichessMatchManagerService.Tests;

public sealed class MatchEventBroadcasterTests
{
    [Fact]
    public void Subscribe_ReturnsUniqueIdsAndChannels()
    {
        MatchEventBroadcaster broadcaster = new();

        (Guid id1, var ch1) = broadcaster.Subscribe("match-1");
        (Guid id2, var ch2) = broadcaster.Subscribe("match-1");

        Assert.NotEqual(id1, id2);
        Assert.NotSame(ch1, ch2);
    }

    [Fact]
    public void Broadcast_DeliversNotificationToSubscribedChannel()
    {
        MatchEventBroadcaster broadcaster = new();
        (_, var channel) = broadcaster.Subscribe("match-1");
        MatchEndedNotification notification = new("white_won", "checkmate");

        broadcaster.Broadcast("match-1", notification);

        Assert.True(channel.Reader.TryRead(out MatchNotification? received));
        Assert.Same(notification, received);
    }

    [Fact]
    public void Broadcast_DeliversToAllSubscribers()
    {
        MatchEventBroadcaster broadcaster = new();
        (_, var ch1) = broadcaster.Subscribe("match-1");
        (_, var ch2) = broadcaster.Subscribe("match-1");
        MatchEndedNotification notification = new("draw", "draw_agreement");

        broadcaster.Broadcast("match-1", notification);

        Assert.True(ch1.Reader.TryRead(out _));
        Assert.True(ch2.Reader.TryRead(out _));
    }

    [Fact]
    public void Broadcast_UnknownMatchId_IsNoOp()
    {
        MatchEventBroadcaster broadcaster = new();

        // Must not throw.
        broadcaster.Broadcast("nonexistent", new MatchEndedNotification("draw", "stalemate"));
    }

    [Fact]
    public void Unsubscribe_RemovesSubscriber_BroadcastNoLongerDelivered()
    {
        MatchEventBroadcaster broadcaster = new();
        (Guid id, var channel) = broadcaster.Subscribe("match-1");

        broadcaster.Unsubscribe("match-1", id);
        broadcaster.Broadcast("match-1", new MatchEndedNotification("draw", "stalemate"));

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void Unsubscribe_UnknownMatchId_IsNoOp()
    {
        MatchEventBroadcaster broadcaster = new();

        // Must not throw.
        broadcaster.Unsubscribe("nonexistent", Guid.NewGuid());
    }

    [Fact]
    public void Unsubscribe_UnknownSubscriptionId_IsNoOp()
    {
        MatchEventBroadcaster broadcaster = new();
        broadcaster.Subscribe("match-1");

        // Must not throw.
        broadcaster.Unsubscribe("match-1", Guid.NewGuid());
    }

    [Fact]
    public void Complete_ClosesAllChannelsForMatch()
    {
        MatchEventBroadcaster broadcaster = new();
        (_, var ch1) = broadcaster.Subscribe("match-1");
        (_, var ch2) = broadcaster.Subscribe("match-1");

        broadcaster.Complete("match-1");

        Assert.True(ch1.Reader.Completion.IsCompleted);
        Assert.True(ch2.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Complete_RemovesMatchEntry_SubsequentBroadcastIsNoOp()
    {
        MatchEventBroadcaster broadcaster = new();
        (_, var channel) = broadcaster.Subscribe("match-1");

        broadcaster.Complete("match-1");
        broadcaster.Broadcast("match-1", new MatchEndedNotification("draw", "stalemate"));

        // Channel was completed — no new items, still completed.
        Assert.True(channel.Reader.Completion.IsCompleted);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void Complete_UnknownMatchId_IsNoOp()
    {
        MatchEventBroadcaster broadcaster = new();

        // Must not throw.
        broadcaster.Complete("nonexistent");
    }
}
