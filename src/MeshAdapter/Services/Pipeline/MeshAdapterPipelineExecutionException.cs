using Meshmakers.Octo.Sdk.Common.Services;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

public class MeshAdapterPipelineExecutionException : PipelineExecutionException
{
    private MeshAdapterPipelineExecutionException()
    {
    }

    private MeshAdapterPipelineExecutionException(string message) : base(message)
    {
    }

    private MeshAdapterPipelineExecutionException(string message, Exception inner) : base(message, inner)
    {
    }


    public static Exception InvalidValue(JToken jToken)
    {
        return new MeshAdapterPipelineExecutionException($"Invalid value: {jToken}");
    }
}
