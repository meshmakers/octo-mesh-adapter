using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Resolves <c>${...}</c> placeholders in a notification template's subject and body.
///
/// The whole substitution for every send path lives here, which is what AB#2569 asks for. The
/// node maps configured data-context paths onto <see cref="NotificationPlaceholderCatalog"/> and
/// hands the reading to <see cref="NotificationPlaceholderResolver"/>; the interesting logic is
/// in those two, and testable without a pipeline.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
[NodeConfiguration(typeof(ResolveNotificationPlaceholdersNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ResolveNotificationPlaceholdersNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ResolveNotificationPlaceholdersNodeConfiguration>();

        var sources = ReadSources(c, dataContext);
        var logoMarkup = ResolveLogoMarkup(c, dataContext);

        PlaceholderValue Lookup(string token)
        {
            var definition = NotificationPlaceholderCatalog.Definitions
                .FirstOrDefault(d => string.Equals(d.Token, token, StringComparison.OrdinalIgnoreCase));

            // A token the catalog does not know cannot be filled by anyone. The editor refuses
            // to save one, so reaching here means the template predates the catalog or was
            // written past it - either way this send path has nothing to offer.
            if (definition == null || !sources.TryGetValue(definition.Source, out var basePath) || basePath == null)
            {
                return PlaceholderValue.NoSource;
            }

            var text = Read(basePath, definition.AttributePath);

            // The fallback is not a guess at missing data - it mirrors what the app displays for
            // the same field, so the mail and the screen cannot disagree about it.
            if (text.Length == 0 && definition.FallbackAttributePath != null)
            {
                text = Read(basePath, definition.FallbackAttributePath);
            }

            return PlaceholderValue.Of(text);

            string Read(string root, string attributePath) =>
                NotificationPlaceholderResolver.Format(
                    dataContext.GetValue($"{root}.Attributes.{attributePath}"),
                    definition.Format,
                    logoMarkup);
        }

        var subject = NotificationPlaceholderResolver.Resolve(dataContext.Get<string>(c.SubjectPath), Lookup);
        var body = NotificationPlaceholderResolver.Resolve(dataContext.Get<string>(c.BodyPath), Lookup);

        var missing = subject.SourceMissing.Concat(body.SourceMissing)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            // The template names something this send path was never given, so a blank here would
            // be a guess - the resolver cannot tell an absent entity from an empty value, and
            // nothing downstream would report either. ForEach@1.ContinueOnError decides whether
            // one bad recipient stops a batch; the refusal itself is never swallowed.
            throw MeshAdapterPipelineExecutionException.PlaceholderSourceMissing(nodeContext, missing);
        }

        var blanks = subject.EmptyByData.Concat(body.EmptyByData)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (blanks.Length > 0)
        {
            // The source was there and the value is genuinely empty - a private customer has no
            // company name. The mail goes, but a letter full of blanks is worth seeing.
            nodeContext.Warning("Placeholder(s) {0} resolved to an empty value", string.Join(", ", blanks));
        }

        // A template whose subject or body was never filled in must stay unset, not become an
        // empty string. NotificationTemplate.BodyTemplate is optional, and SendEMail guards with
        // `?? throw ValueNotSet`, which only a null trips - so writing "" here would send a blank
        // mail with the invoice attached and let ApplyChanges@2 mark the document SENT, which no
        // operator can undo from the app.
        SetWhenSupplied(dataContext, c.SubjectTargetPath, c.SubjectPath, subject.Text);
        SetWhenSupplied(dataContext, c.BodyTargetPath, c.BodyPath, body.Text);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Writes the rendered text, unless the template never carried that part at all.
    /// </summary>
    private static void SetWhenSupplied(IDataContext dataContext, string targetPath, string sourcePath, string text)
    {
        if (dataContext.Get<string>(sourcePath) == null)
        {
            return;
        }

        dataContext.Set(targetPath, text);
    }

    /// <summary>
    /// Which sources this send path supplied, as the base path to read attributes under.
    ///
    /// A source counts as present when its entity is there, probed by <c>RtId</c> - every
    /// runtime entity carries one. That is what keeps an entity whose attributes happen to be
    /// empty distinguishable from one that was never looked up, which is the whole basis of the
    /// empty-versus-refuse decision.
    /// </summary>
    private static Dictionary<PlaceholderSource, string?> ReadSources(
        ResolveNotificationPlaceholdersNodeConfiguration c, IDataContext dataContext)
    {
        return new Dictionary<PlaceholderSource, string?>
        {
            [PlaceholderSource.Customer] = BaseOrNull(dataContext, c.CustomerPath),
            [PlaceholderSource.Community] = BaseOrNull(dataContext, c.CommunityConfigPath),
            [PlaceholderSource.BillingDocument] = BaseOrNull(dataContext, c.BillingDocumentPath)
        };
    }

    private static string? BaseOrNull(IDataContext dataContext, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return string.IsNullOrEmpty(dataContext.Get<string>(path + ".RtId")) ? null : path;
    }

    /// <summary>
    /// What <c>${community.logo}</c> becomes, or null when it can produce nothing: a template
    /// that does not render markup, or a rendering type nobody supplied. The image's own
    /// presence is decided by the catalog lookup, which reads its binary id.
    /// </summary>
    private static string? ResolveLogoMarkup(
        ResolveNotificationPlaceholdersNodeConfiguration c, IDataContext dataContext)
    {
        if (string.IsNullOrWhiteSpace(c.RenderingTypePath))
        {
            return null;
        }

        var renderingType = dataContext.Get<string>(c.RenderingTypePath);
        return string.Equals(renderingType, "Html", StringComparison.OrdinalIgnoreCase)
            ? $"""<img src="cid:{c.LogoContentId}" alt="" />"""
            : null;
    }
}
