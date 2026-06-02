using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
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
                dataContext.Set(c.TargetPath, targetValue, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode);

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
            AttributeValueTypesDto.Int => dataContext.Get<int>(path),
            AttributeValueTypesDto.String => dataContext.Get<string>(path),
            AttributeValueTypesDto.Binary => dataContext.Get<byte>(path),
            AttributeValueTypesDto.Boolean => dataContext.Get<bool>(path),
            AttributeValueTypesDto.DateTime => dataContext.Get<DateTime>(path),
            AttributeValueTypesDto.Double => dataContext.Get<double>(path),
            AttributeValueTypesDto.StringArray => dataContext.Get<string[]>(path),
            AttributeValueTypesDto.IntArray => dataContext.Get<int[]>(path),
            AttributeValueTypesDto.TimeSpan => dataContext.Get<TimeSpan>(path),
            AttributeValueTypesDto.Int64 => dataContext.Get<long>(path),

            /* Not Mapped
                AttributeValueTypesDto.BinaryLinked => dataContext.Get<>(path),
                AttributeValueTypesDto.Record => dataContext.Get<>(path),
                AttributeValueTypesDto.RecordArray => dataContext.Get<>(path),
                AttributeValueTypesDto.Enum => dataContext.Get<>(path),
                AttributeValueTypesDto.DateTimeOffset => dataContext.Get<>(path),
                AttributeValueTypesDto.GeospatialPoint => dataContext.Get<>(path),
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
                AttributeValueTypesDto.Double => value is string s
                    ? double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)
                    : Convert.ChangeType(value, typeof(double)),
                AttributeValueTypesDto.StringArray => Convert.ChangeType(value, typeof(string[])),
                AttributeValueTypesDto.IntArray => Convert.ChangeType(value, typeof(int[])),
                AttributeValueTypesDto.TimeSpan => Convert.ChangeType(value, typeof(TimeSpan)),
                AttributeValueTypesDto.Int64 => Convert.ChangeType(value, typeof(long)),

                /* Not Mapped
                    AttributeValueTypesDto.BinaryLinked => dataContext.Get<>(path),
                    AttributeValueTypesDto.Record => dataContext.Get<>(path),
                    AttributeValueTypesDto.RecordArray => dataContext.Get<>(path),
                    AttributeValueTypesDto.Enum => dataContext.Get<>(path),
                    AttributeValueTypesDto.DateTimeOffset => dataContext.Get<>(path),
                    AttributeValueTypesDto.GeospatialPoint => dataContext.Get<>(path),
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