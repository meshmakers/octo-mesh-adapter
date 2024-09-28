using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.HttpRequests;

public class HttpRequestException : PipelineExecutionException
{
    public HttpRequestException()
    {
    }

    public HttpRequestException(string message) : base(message)
    {
    }

    public HttpRequestException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception RouteAlreadyExists(string uri)
    {
        return new HttpRequestException($"Route '{uri}' already exists");
    }
}