using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.ServiceClient;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
///     AB#7581: the reporting routes require a bearer token of the route tenant, so the node sends the adapter's own
///     service token (the process-wide <see cref="IServiceClientAccessToken" />) on every report request.
/// </summary>
public class GenerateAndStoreReportNodeTests : NodeTestBase
{
    private const string Tenant = "meshtest";

    private static GenerateAndStoreReportNodeConfiguration Config() => new()
    {
        TargetPath = "$.report",
        ReportDefinitionUri = "/billing.trdp",
        FileSystemFolderUri = "/",
        ReportFileNamePrefix = "bill-"
    };

    private static GenerateAndStoreReportNode CreateNode(NodeDelegate next, SequencedHttpMessageHandler handler,
        string? accessToken)
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.TenantId).Returns(Tenant);
        var token = A.Fake<IServiceClientAccessToken>();
        A.CallTo(() => token.AccessToken).Returns(accessToken);
        var options = Microsoft.Extensions.Options.Options.Create(new MeshAdapterConfiguration
        {
            ReportingServiceUrl = "https://reporting.test"
        });
        return new GenerateAndStoreReportNode(next, options, new HttpClient(handler), etlContext, token);
    }

    [Fact]
    public async Task WithAServiceToken_SendsItAsBearer_OnTheRouteOfTheExecutionTenant()
    {
        var (dataContext, nodeContext, next) = PrepareTest(Config());
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"reportUri\":\"bill-1.pdf\"}"));

        await CreateNode(next, handler, "adapter-token").ProcessObjectAsync(dataContext, nodeContext);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.StartsWith($"https://reporting.test/{Tenant}/v1/reports/generateAndStore?", request.RequestUri!.ToString());
        Assert.Equal("Bearer adapter-token", Assert.Single(request.Headers.GetValues("Authorization")));
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WithARelatedEntity_SendsTheBearerToTheRelatedEntityRoute()
    {
        var config = Config() with
        {
            RelatedRtId = Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.Parse("0123456789abcdef01234567"),
            RelatedCkTypeId = new("EnergyCommunity/BillingDocument")
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"reportUri\":\"bill-1.pdf\"}"));

        await CreateNode(next, handler, "adapter-token").ProcessObjectAsync(dataContext, nodeContext);

        var request = handler.Requests.Single();
        Assert.Contains("/generateAndStoreWithRelatedEntity?", request.RequestUri!.ToString());
        Assert.Equal("Bearer adapter-token", Assert.Single(request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task WithoutAServiceToken_SendsNoAuthorizationHeader_AndWarns()
    {
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(Config());
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"reportUri\":\"bill-1.pdf\"}"));

        await CreateNode(next, handler, null).ProcessObjectAsync(dataContext, nodeContext);

        Assert.False(handler.Requests.Single().Headers.Contains("Authorization"));
        A.CallTo(() => logger.Warning(A<string>._, A<string>._,
            A<string>.That.Contains("no service access token"), A<object[]>._)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefusedByTheReportingService_FailsWithTheCredentialHint_AndStopsThePipeline(HttpStatusCode status)
    {
        var (dataContext, nodeContext, next, logger) = PrepareTestWithLogger(Config());
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Status(status));

        await CreateNode(next, handler, null).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Error(A<string>._, A<string>._,
            A<string>.That.Contains("AB#7581"), A<object[]>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }
}
