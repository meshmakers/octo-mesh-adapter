using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(DataMappingNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class DataMappingNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<DataMappingNodeConfiguration>();

        var value = GetValueByConfiguredType(dataContext, nodeContext, c.Path, c.SourceValueType);

        if (value == null)
        {
            await next(dataContext, nodeContext);
            return;
        }

        foreach (var mapping in c.Mappings)
        {
            var mappingSourceValue = ConvertToConfiguredType(nodeContext, mapping.SourceValue, c.SourceValueType);

            if (value.Equals(mappingSourceValue))
            {
                var targetValue = ConvertToConfiguredType(nodeContext, mapping.TargetValue, c.TargetValueType);

                nodeContext.Debug("Mapping value {0} to {1} because of {2}", value, targetValue ?? "",
                    mapping.Description ?? "no description");
                dataContext.SetValueByPath(c.TargetPath, targetValue, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode,
                    RtNewtonsoftSerializer.DefaultSerializer);

                break;
            }
        }


        await next(dataContext, nodeContext);
    }

    private object? GetValueByConfiguredType(IDataContext dataContext, INodeContext nodeContext, string path,
        AttributeValueTypesDto sourceValueType)
    {
        return sourceValueType switch
        {
            AttributeValueTypesDto.Int => dataContext.GetSimpleValueByPath<int>(path),
            AttributeValueTypesDto.String => dataContext.GetSimpleValueByPath<string>(path),
            AttributeValueTypesDto.Binary => dataContext.GetSimpleValueByPath<byte>(path),
            AttributeValueTypesDto.Boolean => dataContext.GetSimpleValueByPath<byte>(path),
            AttributeValueTypesDto.DateTime => dataContext.GetSimpleValueByPath<DateTime>(path),
            AttributeValueTypesDto.Double => dataContext.GetSimpleValueByPath<double>(path),
            AttributeValueTypesDto.StringArray => dataContext.GetSimpleValueByPath<string[]>(path),
            AttributeValueTypesDto.IntArray => dataContext.GetSimpleValueByPath<int[]>(path),
            AttributeValueTypesDto.TimeSpan => dataContext.GetSimpleValueByPath<TimeSpan>(path),
            AttributeValueTypesDto.Int64 => dataContext.GetSimpleValueByPath<long>(path),

            /* Not Mapped
                AttributeValueTypesDto.BinaryLinked => dataContext.GetSimpleValueByPath<>(path),
                AttributeValueTypesDto.Record => dataContext.GetSimpleValueByPath<>(path),
                AttributeValueTypesDto.RecordArray => dataContext.GetSimpleValueByPath<>(path),
                AttributeValueTypesDto.Enum => dataContext.GetSimpleValueByPath<>(path),
                AttributeValueTypesDto.DateTimeOffset => dataContext.GetSimpleValueByPath<>(path),
                AttributeValueTypesDto.GeospatialPoint => dataContext.GetSimpleValueByPath<>(path),
            */

            _ => LogAndThrow(nodeContext, sourceValueType)
        };
    }

    private object? LogAndThrow(INodeContext nodeContext, AttributeValueTypesDto sourceValueType)
    {
        nodeContext.Error("Unknown source value type {0}", sourceValueType);
        throw new ArgumentOutOfRangeException(nameof(sourceValueType), sourceValueType, null);
    }

    private object? ConvertToConfiguredType(INodeContext nodeContext, object? value, AttributeValueTypesDto type)
    {
        try
        {
            return type switch
            {
                AttributeValueTypesDto.Int => Convert.ChangeType(value, typeof(int)),
                AttributeValueTypesDto.String => Convert.ChangeType(value, typeof(string)),
                AttributeValueTypesDto.Binary => Convert.ChangeType(value, typeof(byte)),
                AttributeValueTypesDto.Boolean => Convert.ChangeType(value, typeof(bool)),
                AttributeValueTypesDto.DateTime => Convert.ChangeType(value, typeof(DateTime)),
                AttributeValueTypesDto.Double => Convert.ChangeType(value, typeof(double)),
                AttributeValueTypesDto.StringArray => Convert.ChangeType(value, typeof(string[])),
                AttributeValueTypesDto.IntArray => Convert.ChangeType(value, typeof(int[])),
                AttributeValueTypesDto.TimeSpan => Convert.ChangeType(value, typeof(TimeSpan)),
                AttributeValueTypesDto.Int64 => Convert.ChangeType(value, typeof(long)),

                /* Not Mapped
                    AttributeValueTypesDto.BinaryLinked => dataContext.GetSimpleValueByPath<>(path),
                    AttributeValueTypesDto.Record => dataContext.GetSimpleValueByPath<>(path),
                    AttributeValueTypesDto.RecordArray => dataContext.GetSimpleValueByPath<>(path),
                    AttributeValueTypesDto.Enum => dataContext.GetSimpleValueByPath<>(path),
                    AttributeValueTypesDto.DateTimeOffset => dataContext.GetSimpleValueByPath<>(path),
                    AttributeValueTypesDto.GeospatialPoint => dataContext.GetSimpleValueByPath<>(path),
                */

                _ => LogAndThrow(nodeContext, type)
            };
        }
        catch
        {
            nodeContext.Error("Failed to convert value {0} to {1}", value ?? "", type);
            throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
}