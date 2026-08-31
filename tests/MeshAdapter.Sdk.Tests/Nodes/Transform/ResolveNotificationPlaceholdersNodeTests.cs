using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// The node, against a real data context rather than a faked one - the whole point of these is
/// the decision it makes about what is present, and a fake would only replay the answer.
///
/// The resolver and the catalog are covered separately and purely. What is covered here is what
/// only the node knows: which sources a pipeline supplied, whether a template part was supplied
/// at all, and whether an inline image can be produced.
/// </summary>
public class ResolveNotificationPlaceholdersNodeTests
{
    private const string Customer = """
        {
          "customer": { "RtId": "6a8e", "Attributes": { "Contact": { "Attributes": { "FirstName": "Max" } } } },
          "config": { "RtId": "6977", "Attributes": { "Name": "EEG Musterstadt" } },
          "subject": "Hallo ${customer.firstName}",
          "body": "Gruesse aus ${community.name}"
        }
        """;

    private static async Task<(IDataContext Context, Exception? Error)> RunAsync(
        string json,
        ResolveNotificationPlaceholdersNodeConfiguration configuration)
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(json));
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<ResolveNotificationPlaceholdersNodeConfiguration>())
            .Returns(configuration);
        A.CallTo(() => nodeContext.NodePath).Returns("Test/ResolveNotificationPlaceholders@1");

        var node = new ResolveNotificationPlaceholdersNode((_, _) => Task.CompletedTask);

        try
        {
            await node.ProcessObjectAsync(dataContext, nodeContext);
            return (dataContext, null);
        }
        catch (Exception e)
        {
            return (dataContext, e);
        }
    }

    private static ResolveNotificationPlaceholdersNodeConfiguration Config(
        string? customerPath = "$.customer",
        string? communityPath = "$.config",
        string? billingPath = null,
        string? renderingTypePath = null) => new()
    {
        SubjectPath = "$.subject",
        SubjectTargetPath = "$.renderedSubject",
        BodyPath = "$.body",
        BodyTargetPath = "$.renderedBody",
        CustomerPath = customerPath,
        CommunityConfigPath = communityPath,
        BillingDocumentPath = billingPath,
        RenderingTypePath = renderingTypePath,
    };

    [Fact]
    public async Task A_supplied_source_is_read()
    {
        var (context, error) = await RunAsync(Customer, Config());

        Assert.Null(error);
        Assert.Equal("Hallo Max", context.Get<string>("$.renderedSubject"));
        Assert.Equal("Gruesse aus EEG Musterstadt", context.Get<string>("$.renderedBody"));
    }

    /// <summary>
    /// The case the whole design exists for: a bulk send carries no billing document, so a
    /// billing token has no source and the send is refused rather than blanked.
    /// </summary>
    [Fact]
    public async Task A_token_whose_source_the_pipeline_never_configured_refuses_the_send()
    {
        const string json = """
            {
              "customer": { "RtId": "6a8e", "Attributes": {} },
              "config": { "RtId": "6977", "Attributes": {} },
              "subject": "Abrechnung ${billingDocument.documentNumber}",
              "body": "x"
            }
            """;

        var (_, error) = await RunAsync(json, Config());

        Assert.NotNull(error);
        Assert.Contains("billingDocument.documentNumber", error!.Message, StringComparison.Ordinal);
        // Naming the source, not only the token: four paths are configurable and the operator has
        // to know which one to look at.
        Assert.Contains("BillingDocument (no path configured)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured path that selects nothing - `GetRtEntitiesByType@1` matched no customer for a
    /// manually typed address - is the same as no source at all.
    /// </summary>
    [Fact]
    public async Task A_configured_path_that_selects_nothing_refuses_the_send()
    {
        const string json = """
            { "customer": { "Items": [] }, "config": { "RtId": "6977", "Attributes": {} },
              "subject": "Hallo ${customer.firstName}", "body": "x" }
            """;

        var (_, error) = await RunAsync(json, Config(customerPath: "$.customer.Items[0]"));

        Assert.NotNull(error);
        Assert.Contains("customer.firstName", error!.Message, StringComparison.Ordinal);
        Assert.Contains("Customer ('$.customer.Items[0]' held no entity)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON null is the shape a bare existence check reports as present. It must refuse: a null
    /// entity carries no attribute, so every token reading it would silently blank.
    /// </summary>
    [Fact]
    public async Task A_source_that_is_json_null_refuses_the_send()
    {
        const string json = """
            { "customer": null, "config": { "RtId": "6977", "Attributes": {} },
              "subject": "Hallo ${customer.firstName}", "body": "x" }
            """;

        var (_, error) = await RunAsync(json, Config());

        Assert.NotNull(error);
        Assert.Contains("customer.firstName", error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe asks whether an entity is there, not whether it carries an RtId. It used to read
    /// `path + ".RtId"`, which is an assumption about how a pipeline projects its entities: one
    /// that projects attributes without the id would have been refused although it was supplied.
    /// </summary>
    [Fact]
    public async Task A_source_without_an_RtId_is_still_a_source()
    {
        const string json = """
            { "customer": { "Attributes": { "Contact": { "Attributes": { "FirstName": "Max" } } } },
              "config": { "Attributes": { "Name": "EEG" } },
              "subject": "Hallo ${customer.firstName}", "body": "aus ${community.name}" }
            """;

        var (context, error) = await RunAsync(json, Config());

        Assert.Null(error);
        Assert.Equal("Hallo Max", context.Get<string>("$.renderedSubject"));
    }

    /// <summary>
    /// A present source with an empty attribute is data, not a fault - a private customer has no
    /// company name - so the token renders as nothing and the mail goes.
    /// </summary>
    [Fact]
    public async Task An_empty_attribute_on_a_present_source_substitutes_nothing_and_sends()
    {
        const string json = """
            { "customer": { "RtId": "6a8e", "Attributes": { "Contact": { "Attributes": {} } } },
              "config": { "RtId": "6977", "Attributes": {} },
              "subject": "Firma: ${customer.companyName}!", "body": "x" }
            """;

        var (context, error) = await RunAsync(json, Config());

        Assert.Null(error);
        Assert.Equal("Firma: !", context.Get<string>("$.renderedSubject"));
    }

    /// <summary>
    /// NotificationTemplate.BodyTemplate is optional. Writing "" where the template had nothing
    /// would slip past SendEMail's null guard and send a blank mail whose document is then marked
    /// SENT - so an unsupplied part must stay unsupplied.
    /// </summary>
    [Fact]
    public async Task A_template_part_that_was_never_supplied_stays_unset()
    {
        const string json = """
            { "customer": { "RtId": "6a8e", "Attributes": {} }, "config": { "RtId": "6977", "Attributes": {} },
              "subject": "Betreff" }
            """;

        var (context, error) = await RunAsync(json, Config());

        Assert.Null(error);
        Assert.Equal("Betreff", context.Get<string>("$.renderedSubject"));
        Assert.Null(context.Get<string>("$.renderedBody"));
    }

    [Theory]
    [InlineData("Html", """<img src="cid:community-footer" alt="" />""")]
    [InlineData("Plain", "")]
    public async Task The_logo_resolves_to_markup_only_where_markup_renders(string renderingType, string expected)
    {
        var json = $$"""
            {
              "customer": { "RtId": "6a8e", "Attributes": {} },
              "config": { "RtId": "6977", "Attributes": { "EmailFooterImage": { "BinaryId": "6a91" } } },
              "renderingType": "{{renderingType}}",
              "subject": "s", "body": "[${community.logo}]"
            }
            """;

        var (context, error) = await RunAsync(json, Config(renderingTypePath: "$.renderingType"));

        Assert.Null(error);
        Assert.Equal($"[{expected}]", context.Get<string>("$.renderedBody"));
    }

    /// <summary>
    /// No rendering type configured means the node cannot know whether markup would render, and
    /// an img element in a plain-text mail is worse than no image.
    /// </summary>
    [Fact]
    public async Task Without_a_rendering_type_the_logo_produces_nothing()
    {
        const string json = """
            { "customer": { "RtId": "6a8e", "Attributes": {} },
              "config": { "RtId": "6977", "Attributes": { "EmailFooterImage": { "BinaryId": "6a91" } } },
              "subject": "s", "body": "[${community.logo}]" }
            """;

        var (context, error) = await RunAsync(json, Config());

        Assert.Null(error);
        Assert.Equal("[]", context.Get<string>("$.renderedBody"));
    }
}
