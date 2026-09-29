using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5372 removed <c>MailPostProcessingMode.None</c>, and a pipeline DEFINITION is the second route
/// the value could arrive on (the first is a settings entity — see
/// <c>FromEmailNodeSettingsResolutionTests</c>). These tests pin what each of the two spellings does
/// when the YAML is deserialized, because that is what an operator of an old hand-written definition
/// actually meets.
/// <para>
/// ⚠️ No estate definition spells the mode literally: the accounting seed configures
/// <c>postProcessingModeAttribute</c> and lets the settings entity carry the value, and the Graph
/// channel derives its mode from the done folder. So this route is a hand-made definition only.
/// </para>
/// </summary>
public class MailPostProcessingModeDeserializationTests
{
    private static async Task<FromEmailNodeConfiguration> DeserializeImapAsync(string mode)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterTriggerNode(typeof(FromEmailNode));
        var serializer = services.BuildServiceProvider()
            .GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync(
            $"""
             triggers:
               - type: FromEmail@1
                 serverConfiguration: EmailAccounting
                 postProcessingMode: {mode}
             """);
        return root.Triggers!.OfType<FromEmailNodeConfiguration>().Single();
    }

    [Theory]
    [InlineData("MoveToFolders", MailPostProcessingMode.MoveToFolders)]
    [InlineData("Delete", MailPostProcessingMode.Delete)]
    [InlineData("MarkAsRead", MailPostProcessingMode.MarkAsRead)]
    public async Task TheThreeValidModes_StillDeserializeFromADefinition(
        string spelled, MailPostProcessingMode expected)
    {
        // YamlDotNet reads the NAME, which is why AB#5372 left the remaining members' numbers alone.
        Assert.Equal(expected, (await DeserializeImapAsync(spelled)).PostProcessingMode);
    }

    [Fact]
    public async Task ADefinitionStillSpellingNone_FailsToDeserializeAndNamesTheValue()
    {
        // 🔴 This is the one place AB#5372 cannot word the message itself: the enum is parsed by
        // YamlDotNet inside octo-communication-sdk's YamlPipelineConfigurationSerializer, before any
        // code in this repository sees the configuration. What an operator gets is
        // "Error deserializing pipeline: Exception during deserialization / Requested value 'None'
        // was not found." — it names the value but not the key, the node or the reason. It is loud
        // and it happens on deploy, which is the important half; wording it properly would mean a
        // YamlDotNet type converter in the SDK's serializer. Asserted loosely on purpose: the exact
        // sentence belongs to YamlDotNet, only "it refuses, naming None" is ours to rely on.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => DeserializeImapAsync("None"));

        Assert.Contains("None", ex.Message);
    }

    [Fact]
    public async Task AnExplicitNullMode_ReadsAsUnsetAndIsDerived()
    {
        // The reason the property stays NULLABLE (AB#5372 changed why, not what): a key that is
        // PRESENT and null overwrites a property initializer, and on a non-nullable enum that would
        // now produce the undefined zero value. Nullable, it reads as "unset" — the same answer an
        // omitted key gets — and the derivation decides. markAsRead defaults to true, so this is
        // MarkAsRead and the mailbox keeps being a queue.
        var config = await DeserializeImapAsync("null");

        Assert.Null(config.PostProcessingMode);
        Assert.Equal(MailPostProcessingMode.MarkAsRead,
            FromEmailNode.ResolveEffectivePostProcessingMode(config));
    }
}
