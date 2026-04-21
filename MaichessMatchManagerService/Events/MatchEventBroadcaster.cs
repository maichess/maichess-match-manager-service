using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MaichessMatchManagerService.Events;

internal sealed class MatchEventBroadcaster
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<MatchNotification>>> subscriptions = new();

    internal (Guid Id, Channel<MatchNotification> Channel) Subscribe(string matchId)
    {
        var channel = Channel.CreateUnbounded<MatchNotification>(
            new UnboundedChannelOptions { SingleReader = true });
        var subscriptionId = Guid.NewGuid();
        subscriptions.GetOrAdd(matchId, static _ => new()).TryAdd(subscriptionId, channel);
        return (subscriptionId, channel);
    }

    internal void Unsubscribe(string matchId, Guid subscriptionId)
    {
        if (subscriptions.TryGetValue(matchId, out ConcurrentDictionary<Guid, Channel<MatchNotification>>? channels))
        {
            channels.TryRemove(subscriptionId, out _);
        }
    }

    internal void Broadcast(string matchId, MatchNotification notification)
    {
        if (subscriptions.TryGetValue(matchId, out ConcurrentDictionary<Guid, Channel<MatchNotification>>? channels))
        {
            foreach ((_, Channel<MatchNotification> channel) in channels)
            {
                channel.Writer.TryWrite(notification);
            }
        }
    }

    internal void Complete(string matchId)
    {
        if (subscriptions.TryRemove(matchId, out ConcurrentDictionary<Guid, Channel<MatchNotification>>? channels))
        {
            foreach ((_, Channel<MatchNotification> channel) in channels)
            {
                channel.Writer.Complete();
            }
        }
    }
}
