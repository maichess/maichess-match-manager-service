namespace MaichessMatchManagerService.Services;

internal sealed class InvalidStartPositionException(string fen)
    : Exception($"Invalid start_fen: {fen}");
