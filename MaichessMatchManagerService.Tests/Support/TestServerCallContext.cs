using Grpc.Core;

namespace MaichessMatchManagerService.Tests.Support;

internal sealed class TestServerCallContext : ServerCallContext
{
    private readonly CancellationToken cancellationToken;

    private TestServerCallContext(CancellationToken cancellationToken)
    {
        this.cancellationToken = cancellationToken;
    }

    internal static TestServerCallContext Create(CancellationToken cancellationToken = default) =>
        new(cancellationToken);

    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => Metadata.Empty;
    protected override CancellationToken CancellationTokenCore => cancellationToken;
    protected override Metadata ResponseTrailersCore => Metadata.Empty;
    protected override Status StatusCore { get; set; } = Status.DefaultSuccess;
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => throw new NotSupportedException();

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
        Task.CompletedTask;
}
