using Grpc.Core;

namespace MaichessMatchManagerService.Tests.Support;

internal static class GrpcHelper
{
    internal static AsyncUnaryCall<T> GrpcCall<T>(T response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(Metadata.Empty),
            () => Status.DefaultSuccess,
            () => Metadata.Empty,
            () => { });

    internal static AsyncUnaryCall<T> GrpcFault<T>(RpcException error) =>
        new(
            Task.FromException<T>(error),
            Task.FromResult(Metadata.Empty),
            () => error.Status,
            () => error.Trailers,
            () => { });
}
