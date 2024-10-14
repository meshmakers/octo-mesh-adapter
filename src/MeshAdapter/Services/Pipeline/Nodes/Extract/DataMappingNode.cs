using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("DataMapping", 1)]
public record DataMappingNodeConfiguration : SourceTargetPathNodeConfiguration
{
    public required AttributeValueTypesDto TargetValueType { get; set; }
    public required AttributeValueTypesDto SourceValueType { get; set; }

    /// <summary>
    /// Defines the target type
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public required List<MappingEntry> Mappings { get; set; }
}

public record MappingEntry
{
    public required object SourceValue { get; set; }
    public required object TargetValue { get; set; }
    public string? Description { get; set; }
}

[NodeConfiguration(typeof(DataMappingNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class DataMappingNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<DataMappingNodeConfiguration>();

        var value = GetValueByConfiguredType(dataContext,c.Path, c.SourceValueType);
        
        if (value == null)
        {
            await next(dataContext);
            return;
        }

        foreach (var mapping in c.Mappings)
        {
            var mappingSourceValue = ConvertToConfiguredType(dataContext, mapping.SourceValue, c.SourceValueType);
            
            if (value.Equals(mappingSourceValue))
            {
                var targetValue = ConvertToConfiguredType(dataContext, mapping.TargetValue, c.TargetValueType);
                
                dataContext.NodeContext.Debug("Mapping value {0} to {1} because of {2}", value, targetValue ?? "",
                    mapping.Description ?? "no description");
                dataContext.SetValueByPath(c.TargetPath, targetValue, c.TargetValueKind, c.TargetValueWriteMode,
                    RtNewtonsoftSerializer.DefaultSerializer);

                break;
            }
        }


        await next(dataContext);
    }

    private object? GetValueByConfiguredType(IDataContext dataContext, string path, AttributeValueTypesDto sourceValueType)
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
            
            _ => LogAndThrow(dataContext, sourceValueType)
        };
        
    }

    private object? LogAndThrow(IDataContext context, AttributeValueTypesDto sourceValueType)
    {
        context.NodeContext.Error("Unknown source value type {0}", sourceValueType);
        throw new ArgumentOutOfRangeException(nameof(sourceValueType), sourceValueType, null);
    }

    private object? ConvertToConfiguredType(IDataContext context, object? value, AttributeValueTypesDto type)
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
            
                _ => LogAndThrow(context, type)
            };

        }
        catch
        {
            context.NodeContext.Error("Failed to convert value {0} to {1}", value ?? "", type);
            throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
}