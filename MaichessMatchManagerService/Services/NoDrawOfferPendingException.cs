namespace MaichessMatchManagerService.Services;

internal sealed class NoDrawOfferPendingException : Exception
{
    internal NoDrawOfferPendingException()
        : base("No draw offer is pending.")
    {
    }
}
