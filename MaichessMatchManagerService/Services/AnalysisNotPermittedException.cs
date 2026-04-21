namespace MaichessMatchManagerService.Services;

internal sealed class AnalysisNotPermittedException()
    : Exception("Match is not analyzable");
