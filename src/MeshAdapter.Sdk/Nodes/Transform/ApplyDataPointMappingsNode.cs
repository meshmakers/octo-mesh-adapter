using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
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
    ICkCacheService ckCacheService) : IPipelineNode
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
                valueToMap = EvaluateExpression(mappingExpression, valueToMap, nodeContext, mappingRtId);
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
            dataContext.SetValueByPath(c.TargetPath, updateItems, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }

        await next(dataContext, nodeContext);
    }

    private static object? EvaluateExpression(string expression, object value, INodeContext nodeContext,
        OctoObjectId mappingRtId)
    {
        // Extract numeric value first — fall back to it if the expression fails.
        var hasNumber = TryExtractNumber(value, out var numericValue);
        if (!hasNumber)
        {
            nodeContext.Warning(
                $"DataPointMapping {mappingRtId}: could not extract numeric value from '{value}'");
            return value;
        }

        try
        {
            // Convert C-style ternary (cond ? a : b) to mXparser's if(cond, a, b) syntax.
            var translated = ConvertTernaryToIf(expression);

            var expr = new OctoExpression(translated);
            expr.addArguments(new org.mariuszgromada.math.mxparser.Argument("value", numericValue));

            var result = expr.calculate();
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
                $"DataPointMapping {mappingRtId}: expression '{expression}' failed: {ex.Message}, using numeric value {numericValue}");
            return numericValue;
        }
    }

    /// <summary>
    /// Converts C-style ternary operators (cond ? a : b) to mXparser's if(cond, a, b) syntax.
    /// Handles nesting by scanning for the matching ':' at the same depth level.
    /// </summary>
    private static string ConvertTernaryToIf(string expression)
    {
        if (!expression.Contains('?')) return expression;

        while (true)
        {
            var qIdx = expression.IndexOf('?');
            if (qIdx < 0) break;

            // Find the matching ':' at the same paren depth
            var depth = 0;
            var colonIdx = -1;
            var nestedQCount = 0;
            for (var i = qIdx + 1; i < expression.Length; i++)
            {
                var ch = expression[i];
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == '?' && depth == 0) nestedQCount++;
                else if (ch == ':' && depth == 0)
                {
                    if (nestedQCount == 0) { colonIdx = i; break; }
                    nestedQCount--;
                }
            }

            if (colonIdx < 0) break;

            // Find condition start: scan backwards for balanced parens or start
            var condStart = FindConditionStart(expression, qIdx);
            // Find false-branch end: scan forwards for balanced parens or end
            var falseEnd = FindFalseEnd(expression, colonIdx);

            var condition = expression.Substring(condStart, qIdx - condStart).Trim();
            var trueBranch = expression.Substring(qIdx + 1, colonIdx - qIdx - 1).Trim();
            var falseBranch = expression.Substring(colonIdx + 1, falseEnd - colonIdx - 1).Trim();

            var replacement = $"if({condition}, {trueBranch}, {falseBranch})";
            expression = expression.Substring(0, condStart) + replacement + expression.Substring(falseEnd);
        }

        return expression;
    }

    private static int FindConditionStart(string s, int qIdx)
    {
        var depth = 0;
        for (var i = qIdx - 1; i >= 0; i--)
        {
            var ch = s[i];
            if (ch == ')') depth++;
            else if (ch == '(')
            {
                if (depth == 0) return i + 1;
                depth--;
            }
        }
        return 0;
    }

    private static int FindFalseEnd(string s, int colonIdx)
    {
        var depth = 0;
        for (var i = colonIdx + 1; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return s.Length;
    }

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
        if (config.SourceRtIdPath == null || dataContext.Current == null) return null;

        return dataContext.GetComplexObjectByPath<OctoObjectId?>(config.SourceRtIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);
    }

    private static RtCkId<CkTypeId>? GetSourceCkTypeId(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (config.SourceCkTypeIdPath == null || dataContext.Current == null) return null;

        return dataContext.GetComplexObjectByPath<RtCkId<CkTypeId>?>(config.SourceCkTypeIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);
    }

    private static object? GetSourceValue(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (config.SourceValuePath == null || dataContext.Current == null) return null;

        var token = dataContext.Current.SelectToken(config.SourceValuePath);
        return token?.ToObject<object>();
    }

    private static string? GetSourceStateName(IDataContext dataContext,
        ApplyDataPointMappingsNodeConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.SourceStateNamePath) || dataContext.Current == null) return null;

        var token = dataContext.Current.SelectToken(config.SourceStateNamePath);
        var str = token?.ToObject<string>();
        return string.IsNullOrWhiteSpace(str) ? null : str;
    }
}
