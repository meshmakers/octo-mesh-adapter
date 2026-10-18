using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(DataMappingNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class DataMappingNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<DataMappingNodeConfiguration>();
        // AB#5538: mapping compares and converts values - a Secret can be neither source nor target.
        PipelineSecretValues.ThrowIfSecretValueType(nodeContext, c.SourceValueType, "sourceValueType");
        PipelineSecretValues.ThrowIfSecretValueType(nodeContext, c.TargetValueType, "targetValueType");
        PipelineSecretValues.ThrowIfSecretMarker(nodeContext, dataContext, c.Path);

        // AB#6589: the shape of the path decides. A path that can address several values (wildcard,
        // recursive descent, filter) maps every addressed element in place; every other path takes
        // the single-value route below exactly as before.
        if (IsMultiMatchPath(c.Path))
        {
            await MapAllMatchesAsync(dataContext, nodeContext, c);
            await next(dataContext, nodeContext);
            return;
        }

        var value = GetValueByConfiguredType(dataContext, nodeContext, c.Path, c.SourceValueType);

        if (value == null)
        {
            await next(dataContext, nodeContext);
            return;
        }

        var mappingLookup = new MappingLookup(this, nodeContext, c);
        if (mappingLookup.TryFind(value, out var targetValue, out var description))
        {
            nodeContext.Debug("Mapping value {0} to {1} because of {2}", value, targetValue ?? "",
                description ?? "no description");
            dataContext.Set(c.TargetPath, targetValue, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Maps every element a multi-match <c>path</c> addresses, in place. Elements without a matching
    /// mapping stay as they are, null elements are skipped, everything outside the matches is not
    /// touched. A path that matches nothing is a warning, not a failure.
    /// </summary>
    private async Task MapAllMatchesAsync(IDataContext dataContext, INodeContext nodeContext,
        DataMappingNodeConfiguration c)
    {
        EnsureInPlaceTarget(nodeContext, c);
        EnsureDefaultWriteSettings(nodeContext, c);

        // One lookup for the whole run: the mapping entries are converted once, on first use, by the
        // same conversion method the single-value route uses (AB#2141 / AB#6074 replace it in one place).
        var mappingLookup = new MappingLookup(this, nodeContext, c);
        var matchCount = 0;
        var mappedCount = 0;

        await dataContext.UpdateMatchesAsync(c.Path, matchCtx =>
        {
            var index = matchCount++;

            var kind = matchCtx.GetKind("$");
            if (kind is DataKind.Null or DataKind.Undefined)
            {
                return Task.CompletedTask;
            }

            // A Secret marker is an object; a scalar source type cannot be one of the elements.
            if (kind == DataKind.Object && PipelineSecretValues.IsSecretMarker(matchCtx, "$"))
            {
                throw PipelineExecutionException.SecretNotSupported(nodeContext.NodePath, c.Path);
            }

            object? value;
            try
            {
                value = GetValueByConfiguredType(matchCtx, nodeContext, "$", c.SourceValueType);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException
                                          or OverflowException or InvalidCastException)
            {
                throw MeshAdapterPipelineExecutionException.DataMappingElementNotConvertible(nodeContext,
                    c.Path, index, c.SourceValueType.ToString(), e);
            }

            if (value == null || !mappingLookup.TryFind(value, out var targetValue, out _))
            {
                return Task.CompletedTask;
            }

            // The sub-context root is the element; UpdateMatchesAsync writes it back to the element's
            // own position in this context.
            matchCtx.Set("$", JsonSerializer.SerializeToNode(targetValue, SystemTextJsonOptions.Default));
            mappedCount++;
            return Task.CompletedTask;
        });

        if (matchCount == 0)
        {
            nodeContext.Warning("No elements found at path '{0}'", c.Path);
            return;
        }

        // One summary instead of one debug line per element: a field case maps ~123,000 of them.
        nodeContext.Debug("Mapped {0} of {1} elements at '{2}'", mappedCount, matchCount, c.Path);
    }

    /// <summary>
    /// A multi-match <c>path</c> maps in place: <c>targetPath</c> is the same path or left out (its
    /// default is the document root, which is what "left out" looks like here).
    /// </summary>
    private static void EnsureInPlaceTarget(INodeContext nodeContext, DataMappingNodeConfiguration c)
    {
        var targetPath = c.TargetPath;
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Trim() == "$")
        {
            return;
        }

        if (JsonNodePath.NormalizePathOrRelative(targetPath) == JsonNodePath.NormalizePathOrRelative(c.Path))
        {
            return;
        }

        nodeContext.Error("Multi-match path '{0}' cannot be mapped to a different target path '{1}'", c.Path,
            targetPath);
        throw MeshAdapterPipelineExecutionException.DataMappingMultiMatchNeedsInPlaceTarget(nodeContext, c.Path,
            targetPath);
    }

    /// <summary>
    /// The in-place write has no use for the three write settings of the single-value route, and silently
    /// ignoring a setting an author configured (<c>targetValueWriteMode: IgnoreExisting</c> would mean
    /// "never map" there) is worse than refusing it: anything but the default is rejected.
    /// </summary>
    private static void EnsureDefaultWriteSettings(INodeContext nodeContext, DataMappingNodeConfiguration c)
    {
        if (c.TargetValueWriteMode != TargetValueWriteModes.Overwrite)
        {
            throw MeshAdapterPipelineExecutionException.DataMappingMultiMatchIgnoresWriteSetting(nodeContext,
                c.Path, "targetValueWriteMode", c.TargetValueWriteMode.ToString(),
                TargetValueWriteModes.Overwrite.ToString());
        }

        if (c.TargetValueKind != ValueKinds.Simple)
        {
            throw MeshAdapterPipelineExecutionException.DataMappingMultiMatchIgnoresWriteSetting(nodeContext,
                c.Path, "targetValueKind", c.TargetValueKind.ToString(), ValueKinds.Simple.ToString());
        }

        if (c.DocumentMode != DocumentModes.Extend)
        {
            throw MeshAdapterPipelineExecutionException.DataMappingMultiMatchIgnoresWriteSetting(nodeContext,
                c.Path, "documentMode", c.DocumentMode.ToString(), DocumentModes.Extend.ToString());
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> contains a wildcard, a recursive descent or a filter segment.
    /// The same two-stage test the SDK applies to <c>GetArray&lt;T&gt;</c> (AB#5351): a cheap character
    /// test first, so an ordinary path costs nothing, then the parser decides.
    /// </summary>
    private static bool IsMultiMatchPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (!path.Contains('*') && !path.Contains('?') && !path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in JsonPathParser.Parse(JsonNodePath.NormalizePathOrRelative(path)).Segments)
        {
            if (segment is WildcardSegment or RecursiveDescentSegment or FilterSegment)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// The one conversion of a configured value to the source/target value type, shared by the
    /// single-value route and the element-wise route.
    /// </summary>
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

    /// <summary>
    /// Finds the mapping entry for a value. Entries are converted lazily and at most once per run, in
    /// configuration order, so what the single-value route did - an entry behind the first match is never
    /// converted, an unconvertible entry only fails when it is reached - stays true, and a run over many
    /// elements does not convert the same entry again for every one of them.
    /// </summary>
    private sealed class MappingLookup(DataMappingNode owner, INodeContext nodeContext,
        DataMappingNodeConfiguration config)
    {
        private readonly (bool Converted, object? Source)[] _sources = new (bool, object?)[config.Mappings.Count];
        private readonly (bool Converted, object? Target)[] _targets = new (bool, object?)[config.Mappings.Count];

        public bool TryFind(object value, out object? targetValue, out string? description)
        {
            for (var i = 0; i < config.Mappings.Count; i++)
            {
                var mapping = config.Mappings[i];
                if (!_sources[i].Converted)
                {
                    _sources[i] = (true,
                        owner.ConvertToConfiguredType(nodeContext, mapping.SourceValue, config.SourceValueType));
                }

                if (!value.Equals(_sources[i].Source))
                {
                    continue;
                }

                if (!_targets[i].Converted)
                {
                    _targets[i] = (true,
                        owner.ConvertToConfiguredType(nodeContext, mapping.TargetValue, config.TargetValueType));
                }

                targetValue = _targets[i].Target;
                description = mapping.Description;
                return true;
            }

            targetValue = null;
            description = null;
            return false;
        }
    }
}
