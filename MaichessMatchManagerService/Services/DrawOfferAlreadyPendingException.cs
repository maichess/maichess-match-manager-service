namespace MaichessMatchManagerService.Services;

internal sealed class DrawOfferAlreadyPendingException : Exception
{
    internal DrawOfferAlreadyPendingException()
        : base("A draw offer is already pending.")
    {
    }
}
