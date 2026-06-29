using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Evaluates DataPointMapping entities associated with a source entity via MapsFrom,
/// applies optional mapping expressions, and produces update items for the mapped target entities.
/// </summary>
[NodeConfiguration(typeof(ApplyDataPointMappingsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class ApplyDataPointMappingsNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ICkCacheService ckCacheService,
    IFormulaEngine formulaEngine) : IPipelineNode
{
    private static readonly RtCkId<CkAssociationRoleId> MapsFromRoleId = new("System.Communication/MapsFrom");
    private static readonly RtCkId<CkAssociationRoleId> MapsToRoleId = new("System.Communication/MapsTo");
    private static readonly RtCkId<CkTypeId> DataPointMappingCkTypeId = new("System.Communication/DataPointMapping");

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ApplyDataPointMappingsNodeConfiguration>();

        var sourceRtId = GetSourceRtId(dataContext, c);
        var sourceCkTypeId = GetSourceCkTypeId(dataContext, c);
        var sourceValue = GetSourceValue(dataContext, c);
        var sourceStateName = GetSourceStateName(dataContext, c);

        if (sourceRtId == null || sourceCkTypeId == null)
        {
            nodeContext.Warning("Source entity RtId or CkTypeId not available, skipping mapping evaluation");
            await next(dataContext, nodeContext);
            return;
        }

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Step 1: Find DataPointMapping entities via inbound MapsFrom association.
        // The DataPointMapping entity owns the MapsFrom association pointing to the source entity,
        // so from the source's perspective it's INBOUND.
        var sourceAssocsResult = await etlContext.TenantRepository.GetRtAssociationsAsync(
            session,
            new RtEntityId(sourceCkTypeId, sourceRtId.Value),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, MapsFromRoleId,
                targetTypeId: DataPointMappingCkTypeId));

        var mappingAssociations = sourceAssocsResult.Items.ToList();
        if (mappingAssociations.Count == 0)
        {
            nodeContext.Debug("No DataPointMappings found for source entity");
            await next(dataContext, nodeContext);
            return;
        }

        var updateItems = new List<EntityUpdateInfo<RtEntity>>();

        foreach (var mappingAssoc in mappingAssociations)
        {
            // For inbound associations, the origin (not target) is the related entity.
            // DataPointMapping → (MapsFrom) → Source. From source's inbound perspective,
            // the origin of the association is the DataPointMapping.
            var mappingRtId = mappingAssoc.OriginRtId;

            // Load the DataPointMapping entity to read attributes
            var mappingResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                session, DataPointMappingCkTypeId, new[] { mappingRtId },
                RtEntityQueryOptions.Create());

            var mappingEntity = mappingResult.Items.FirstOrDefault();
            if (mappingEntity == null) continue;

            var enabled = mappingEntity.GetAttributeValueOrDefault<bool>("Enabled") ?? true;
            if (!enabled) continue;

            var sourceAttributePath = mappingEntity.GetAttributeValueOrDefault("SourceAttributePath") as string;
            var mappingExpression = mappingEntity.GetAttributeValueOrDefault("MappingExpression") as string;
            var targetAttributePath = mappingEntity.GetAttributeValueOrDefault("TargetAttributePath") as string;

            // When a state name is provided from the incoming data, only apply mappings
            // whose SourceAttributePath matches the state name. This supports multi-state
            // polling where a single control emits multiple state updates and each mapping
            // targets one specific state.
            if (!string.IsNullOrWhiteSpace(sourceStateName))
            {
                var effectiveSourceAttr = string.IsNullOrWhiteSpace(sourceAttributePath)
                    ? "currentValue"
                    : sourceAttributePath;
                if (!string.Equals(effectiveSourceAttr, sourceStateName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(targetAttributePath))
            {
                nodeContext.Warning($"DataPointMapping {mappingRtId}: targetAttributePath is empty, skipping");
                continue;
            }

            // Step 2: Get MapsTo target association (no related type filter - target can be any concrete type)
            var targetAssocsResult = await etlContext.TenantRepository.GetRtAssociationsAsync(
                session,
                new RtEntityId(DataPointMappingCkTypeId, mappingRtId),
                RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, MapsToRoleId));

            var targetAssoc = targetAssocsResult.Items.FirstOrDefault();
            if (targetAssoc == null)
            {
                nodeContext.Warning($"DataPointMapping {mappingRtId}: no MapsTo target association found, skipping");
                continue;
            }

            var targetCkTypeId = targetAssoc.TargetCkTypeId;
            var targetRtId = targetAssoc.TargetRtId;

            // Step 3: Resolve the value to map.
            // When sourceStateName is active, the incoming sourceValue is already the correct
            // state-specific value (polled via the state's ExternalId). No entity lookup needed.
            // Only read from entity when sourceAttributePath refers to a direct CK attribute
            // (e.g. "currentValue") and no state name filtering is active.
            object? valueToMap = sourceValue;

            if (!string.IsNullOrWhiteSpace(sourceAttributePath) && string.IsNullOrWhiteSpace(sourceStateName))
            {
                // Only attempt entity attribute lookup for direct CK attributes (e.g. "currentValue").
                // For DataPoint names (e.g. "tempActual") that don't exist as CK attributes,
                // fall back to the incoming sourceValue which is the polled value.
                try
                {
                    var sourceEntityResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                        session,
                        sourceCkTypeId,
                        new[] { sourceRtId.Value },
                        RtEntityQueryOptions.Create());

                    var sourceEntity = sourceEntityResult.Items.FirstOrDefault();
                    if (sourceEntity != null)
                    {
                        valueToMap = RtPathEvaluator.GetValue(ckCacheService, etlContext.TenantId,
                            sourceEntity, sourceAttributePath);
                    }
                }
                catch
                {
                    // Attribute not found on CK type — use sourceValue as fallback
                    nodeContext.Debug($"DataPointMapping {mappingRtId}: '{sourceAttributePath}' not a CK attribute, using polled value");
                }
            }

            // Step 4: Evaluate mapping expression if present
            if (!string.IsNullOrWhiteSpace(mappingExpression) && valueToMap != null)
            {
                // Cast the formula result to the target attribute's CK value type so non-Double
                // targets (Boolean/Int/Int64/DateTime) receive a correctly typed value. The
                // AttributeValueConverter does not coerce a raw double into e.g. a bool
                // (bool.TryParse("1") == false), so without this a boolean target would never be set.
                var resultType = ResolveFormulaResultType(targetCkTypeId, targetAttributePath);
                valueToMap = EvaluateExpression(mappingExpression, valueToMap, resultType, nodeContext, mappingRtId);
            }
            else if (valueToMap != null && TryExtractNumber(valueToMap, out var numericValue))
            {
                // No expression: still try to extract number from string values with units
                // (e.g. "27.0 %" -> 27.0) so numeric target attributes receive the right type.
                valueToMap = numericValue;
            }

            if (valueToMap == null) continue;

            // Step 5: Create update info for the target entity
            var rtEntity = new RtEntity
            {
                CkTypeId = targetCkTypeId,
                RtChangedDateTime = DateTime.UtcNow
            };

            RtPathEvaluator.SetValue(ckCacheService, etlContext.TenantId,
                rtEntity, targetAttributePath, valueToMap);

            var updateItem = EntityUpdateInfo<RtEntity>.CreateUpdate(
                new RtEntityId(targetCkTypeId, targetRtId), rtEntity);
            updateItems.Add(updateItem);

            nodeContext.Debug(
                $"DataPointMapping {mappingRtId}: {sourceAttributePath ?? "(direct)"} -> " +
                $"{targetCkTypeId}@{targetRtId}.{targetAttributePath} = {valueToMap}");
        }

        if (updateItems.Count > 0)
        {
            dataContext.Set(c.TargetPath, updateItems, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private object? EvaluateExpression(string expression, object value, FormulaResultType? resultType,
        INodeContext nodeContext, OctoObjectId mappingRtId)
    {
        // Extract numeric value first — fall back to it if the expression fails.
        var hasNumber = TryExtractNumber(value, out var numericValue);
        if (!hasNumber)
        {
            nodeContext.Warning(
                $"DataPointMapping {mappingRtId}: could not extract numeric value from '{value}'");
            return value;
        }

        var args = new Dictionary<string, double> { ["value"] = numericValue };

        try
        {
            // Non-Double targets need a typed result: the formula engine casts the numeric result
            // back to Boolean/Int/Int64/DateTime. Double and unresolved targets keep the raw double
            // path (byte-identical to the previous behaviour).
            if (resultType is { } rt && rt != FormulaResultType.Double)
            {
                var typed = formulaEngine.Evaluate(expression, args, rt);
                if (typed == null)
                {
                    nodeContext.Warning(
                        $"DataPointMapping {mappingRtId}: expression '{expression}' returned null/NaN for value {value}, skipping");
                }

                return typed;
            }

            var result = formulaEngine.EvaluateRaw(expression, args);
            if (double.IsNaN(result))
            {
                nodeContext.Warning(
                    $"DataPointMapping {mappingRtId}: expression '{expression}' returned NaN for value {value}, using numeric value {numericValue}");
                return numericValue;
            }

            return result;
        }
        catch (Exception ex)
        {
            nodeContext.Warning(
                $"DataPointMapping {mappingRtId}: expression '{expression}' failed: {ex.Message}");
            // For a typed (non-Double) target, a raw double fallback would not convert cleanly
            // (e.g. into a bool), so skip the write instead of producing a wrong-typed value.
            return resultType is { } rt2 && rt2 != FormulaResultType.Double ? null : numericValue;
        }
    }

    /// <summary>
    /// Resolves the target attribute's CK value type to a <see cref="FormulaResultType"/> so a
    /// formula result can be cast to the right CLR type before it is written. Returns <c>null</c>
    /// for nested/navigation paths and for non-scalar / string / record types — those keep the raw
    /// double path. Enums are treated as <see cref="FormulaResultType.Int"/> (their numeric key).
    /// </summary>
    private FormulaResultType? ResolveFormulaResultType(RtCkId<CkTypeId> targetCkTypeId,
        string targetAttributePath)
    {
        // Only simple, single-segment attribute names are resolved here; anything with navigation
        // (. [ ] -> ::) falls back to the raw double path.
        if (targetAttributePath.IndexOfAny(['.', '[', ']', '-', ':']) >= 0)
        {
            return null;
        }

        if (!ckCacheService.TryGetRtCkType(etlContext.TenantId, targetCkTypeId, out var ckTypeGraph)
            || !ckTypeGraph.AllAttributesByName.TryGetValue(targetAttributePath, out var attribute))
        {
            return null;
        }

        return MapValueTypeToFormulaResultType(attribute.ValueType);
    }

    /// <summary>
    /// Maps a CK scalar <see cref="AttributeValueTypesDto"/> to the formula engine's
    /// <see cref="FormulaResultType"/>. Returns <c>null</c> for string / record / array / geospatial
    /// and other non-scalar types, signalling the caller to keep the raw double path. Enums map to
    /// <see cref="FormulaResultType.Int"/> (their numeric key). Pure, side-effect-free testable seam.
    /// </summary>
    internal static FormulaResultType? MapValueTypeToFormulaResultType(AttributeValueTypesDto valueType) =>
        valueType switch
        {
            AttributeValueTypesDto.Boolean => FormulaResultType.Boolean,
            AttributeValueTypesDto.Int => FormulaResultType.Int,
            AttributeValueTypesDto.Enum => FormulaResultType.Int,
            AttributeValueTypesDto.Int64 => FormulaResultType.Int64,
            AttributeValueTypesDto.Double => FormulaResultType.Double,
            AttributeValueTypesDto.DateTime => FormulaResultType.DateTime,
            _ => null
        };

    /// <summary>
    /// Extracts a numeric value from an input that may be a number, a string with a unit
    /// (e.g. "27.0 %", "-1773.0 W"), or already a numeric type.
    /// </summary>
    private static bool TryExtractNumber(object value, out double result)
    {
        result = 0;
        if (value is double d) { result = d; return true; }
        if (value is float f) { result = f; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }

        var str = value.ToString();
        if (string.IsNullOrWhiteSpace(str)) return false;

        // Match leading number (optional sign, digits, decimal point) — ignore trailing unit
        var match = System.Text.RegularExpressions.Regex.Match(str,
            @"^\s*(-?\d+(?:[.,]\d+)?)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success) return false;

        var numberText = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(numberText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static OctoObjectId? GetSourceRtId(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (config.SourceRtIdPath == null) return null;

        return dataContext.Get<OctoObjectId?>(config.SourceRtIdPath);
    }

    private static RtCkId<CkTypeId>? GetSourceCkTypeId(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (config.SourceCkTypeIdPath == null) return null;

        return dataContext.Get<RtCkId<CkTypeId>?>(config.SourceCkTypeIdPath);
    }

    private static object? GetSourceValue(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (config.SourceValuePath == null) return null;

        // Scalars (bool / long / double / DateTime / string) box via the shared JsonScalar rules
        // exposed through GetValue — identical to the former hand-rolled UnwrapJsonNode ladder.
        var scalar = dataContext.GetValue(config.SourceValuePath);
        if (scalar != null) return scalar;

        // Object / array values are serialized to their compact JSON string (legacy parity:
        // the former code returned node.ToJsonString() with the default encoder). GetValue
        // returns null for these kinds, so handle them explicitly here.
        return dataContext.GetKind(config.SourceValuePath) switch
        {
            DataKind.Object or DataKind.Array => CompactJson(dataContext, config.SourceValuePath),
            _ => null
        };
    }

    private static string? CompactJson(IDataContext dataContext, string path)
    {
        using var stream = new MemoryStream();
        dataContext.WriteJsonTo(path, stream);
        return stream.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GetSourceStateName(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.SourceStateNamePath)) return null;

        var str = dataContext.Get<string>(config.SourceStateNamePath);
        return string.IsNullOrWhiteSpace(str) ? null : str;
    }
}
