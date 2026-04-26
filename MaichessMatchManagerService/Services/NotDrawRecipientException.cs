namespace MaichessMatchManagerService.Services;

internal sealed class NotDrawRecipientException : Exception
{
    internal NotDrawRecipientException()
        : base("Only the recipient of a draw offer may accept it.")
    {
    }
}
