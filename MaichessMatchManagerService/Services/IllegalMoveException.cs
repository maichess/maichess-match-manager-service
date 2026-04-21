namespace MaichessMatchManagerService.Services;

internal sealed class IllegalMoveException(string reason)
    : Exception(reason)
{
    internal string Reason { get; } = reason;
}
