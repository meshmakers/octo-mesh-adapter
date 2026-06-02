using System.Globalization;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that checks if an entity with a matching attribute value already exists.
/// Writes true/false to targetPath and optionally the existing entity to existingEntityPath.
/// </summary>
[NodeConfiguration(typeof(CheckDuplicateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CheckDuplicateNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<CheckDuplicateNodeConfiguration>();

        // Read the dedup key and coerce to its string form. The pre-migration node used
        // SelectToken(path)?.ToString(), which stringified any scalar (42 -> "42",
        // true -> "True"); Get<string>(path) throws on a numeric/object value path. Numeric IDs
        // are a routine dedup key, so restore the coercion via the path-only surface.
        var value = CoerceToString(dataContext, config.ValuePath);
        if (string.IsNullOrEmpty(value))
        {
            nodeContext.Debug("No value found for duplicate check, skipping");
            dataContext.Set(config.TargetPath, false, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode);
            await next(dataContext, nodeContext);
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(config.AttributeName, FieldFilterOperator.Equals, value);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, config.CkTypeId, queryOptions, 0, 1);
        await session.CommitTransactionAsync();

        var isDuplicate = result.TotalCount > 0;

        dataContext.Set(config.TargetPath, isDuplicate, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        if (isDuplicate && !string.IsNullOrEmpty(config.ExistingEntityPath))
        {
            var existingEntity = result.Items.FirstOrDefault();
            if (existingEntity != null)
            {
                dataContext.Set(config.ExistingEntityPath, existingEntity, DocumentModes.Extend,
                    ValueKinds.Simple, TargetValueWriteModes.Overwrite);
            }
        }

        nodeContext.Info(isDuplicate
            ? $"Duplicate found: entity with {config.AttributeName}='{value}' already exists"
            : $"No duplicate found for {config.AttributeName}='{value}'");

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Coerces the value at <paramref name="path"/> to its string form, mirroring the
    /// pre-migration <c>JToken.ToString()</c>: scalars stringify (number → "42", bool → "True",
    /// string → itself) and objects/arrays render as compact JSON. Returns null for a
    /// missing/null value. Dates are NOT parsed (parseDateStrings:false) so a date-like
    /// string is compared verbatim, matching the legacy SelectToken().ToString() behaviour.
    /// </summary>
    private static string? CoerceToString(IDataContext dataContext, string path)
    {
        // Scalars first via the natural-CLR read (no date parsing — string keys stay verbatim).
        var scalar = dataContext.GetValue(path, parseDateStrings: false);
        if (scalar != null)
        {
            return scalar switch
            {
                bool b => b ? "True" : "False",
                string s => s,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => scalar.ToString()
            };
        }

        // Object / array values render as their compact JSON string (legacy parity).
        return dataContext.GetKind(path) switch
        {
            DataKind.Object or DataKind.Array => CompactJson(dataContext, path),
            _ => null
        };
    }

    private static string? CompactJson(IDataContext dataContext, string path)
    {
        using var stream = new MemoryStream();
        dataContext.WriteJsonTo(path, stream);
        return stream.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
