using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

/// <summary>
///     The settings of a <c>System.Ai/AiPromptTemplate</c> entity with <c>PromptPurpose</c>
///     <c>KnowledgeGeneration</c>, as read by <c>AnthropicAiQuery@1</c> (AB#8498). Every value is
///     optional; absent values fall through to the AiConfiguration and the node properties.
/// </summary>
/// <param name="Name">Well-known name the template was loaded by.</param>
/// <param name="SystemPrompt">The template's <c>SystemPromptFragment</c>.</param>
/// <param name="UserTemplate">The template's <c>UserTemplate</c> (instruction / question text).</param>
/// <param name="FewShotExamples">The template's <c>FewShotExamples</c>.</param>
/// <param name="ModelId">The template's <c>DefaultModelId</c>.</param>
/// <param name="Temperature">The template's <c>Temperature</c>, only when within 0.0 to 1.0.</param>
/// <param name="MaxTokens">The template's <c>MaxTokens</c>, only when positive.</param>
/// <param name="TemplateVersion">The template's <c>TemplateVersion</c>.</param>
internal sealed record AiPromptTemplateSettings(
    string Name,
    string? SystemPrompt,
    string? UserTemplate,
    string? FewShotExamples,
    string? ModelId,
    double? Temperature,
    int? MaxTokens,
    int? TemplateVersion);

/// <summary>
///     Loads an <c>AiPromptTemplate</c> by well-known name from the pipeline's configuration
///     hand-over (AB#8498, decision Odin 2026-11-09): the template reaches the pipeline like an
///     AiConfiguration does — through a <c>Uses</c> association of the pipeline — and the adapter
///     caches it at pipeline registration, so a prompt change takes effect after the pipeline is
///     redeployed.
/// </summary>
internal static class AiPromptTemplateResolver
{
    /// <summary>Full name of the CK type a prompt template must have (without element version).</summary>
    internal const string AiPromptTemplateTypeFullName = "System.Ai/AiPromptTemplate";

    private const string AiPromptTemplateModelId = "System.Ai";
    private const string AiPromptTemplateTypeName = "AiPromptTemplate";

    /// <summary>Key of <c>PromptTemplatePurpose.KnowledgeGeneration</c> in System.Ai 4.7.0.</summary>
    internal const int KnowledgeGenerationPurposeKey = 1;

    internal const string KnowledgeGenerationPurposeName = "KnowledgeGeneration";

    /// <summary>
    ///     Returns the template named <paramref name="templateName" />, or <c>null</c> when no name is
    ///     configured. Fails with an <see cref="InvalidOperationException" /> naming the template when
    ///     it is not handed over, is of another CK type or is not a KnowledgeGeneration template.
    /// </summary>
    internal static AiPromptTemplateSettings? Resolve(IGlobalConfiguration globalConfiguration,
        string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return null;
        }

        if (!globalConfiguration.IsDefined(templateName))
        {
            throw new InvalidOperationException(
                $"AiPromptTemplate '{templateName}' not found. The pipeline needs a 'Uses' association to the " +
                $"{AiPromptTemplateTypeFullName} entity with the well-known name '{templateName}'; a new or " +
                "changed template takes effect after the pipeline is redeployed.");
        }

        var typeId = globalConfiguration.GetConfigurationTypeId(templateName);
        // Compared by model and type name: the element version ("AiPromptTemplate-1") is not part of
        // the identity a pipeline author means.
        if (typeId != null && !typeId.IsEmpty &&
            (!string.Equals(typeId.ModelId, AiPromptTemplateModelId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(typeId.ElementId.Name, AiPromptTemplateTypeName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Configuration '{templateName}' is a '{typeId.ModelId}/{typeId.ElementId.Name}', not an " +
                $"{AiPromptTemplateTypeFullName}.");
        }

        var attributes = ConfigurationSettingsReader.TryGetAttributes(globalConfiguration, templateName)
                         ?? throw new InvalidOperationException(
                             $"AiPromptTemplate '{templateName}' could not be read (malformed configuration).");

        if (!IsKnowledgeGeneration(attributes))
        {
            throw new InvalidOperationException(
                $"AiPromptTemplate '{templateName}' does not have PromptPurpose {KnowledgeGenerationPurposeName}; " +
                "only KnowledgeGeneration templates can be used by AnthropicAiQuery@1.");
        }

        var temperature = ConfigurationSettingsReader.ReadDouble(attributes, "Temperature");
        return new AiPromptTemplateSettings(
            templateName,
            ConfigurationSettingsReader.ReadString(attributes, "SystemPromptFragment"),
            ConfigurationSettingsReader.ReadString(attributes, "UserTemplate"),
            ConfigurationSettingsReader.ReadString(attributes, "FewShotExamples"),
            ConfigurationSettingsReader.ReadString(attributes, "DefaultModelId"),
            temperature is >= 0.0 and <= 1.0 ? temperature : null,
            ConfigurationSettingsReader.ReadPositiveInt(attributes, "MaxTokens"),
            ConfigurationSettingsReader.ReadInt(attributes, "TemplateVersion"));
    }

    /// <summary>
    ///     A CK enum attribute arrives as its key (number); the name is accepted as well so a
    ///     serializer change on the controller side cannot lock every template out. An absent
    ///     purpose is the CK default <c>SessionSystemPrompt</c> and therefore rejected.
    /// </summary>
    private static bool IsKnowledgeGeneration(JsonElement attributes)
    {
        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, "PromptPurpose", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.TryGetInt32(out var key) &&
                                        key == KnowledgeGenerationPurposeKey,
                JsonValueKind.String => string.Equals(property.Value.GetString(), KnowledgeGenerationPurposeName,
                                            StringComparison.OrdinalIgnoreCase) ||
                                        property.Value.GetString() == "1",
                _ => false
            };
        }

        return false;
    }
}
