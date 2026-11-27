using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     <c>GetNumberRangeGapReport@1</c> maps the engine's gap report into the data context: one engine call with the
///     session of the node's identity, the range and fiscal year from the configuration or from the data context, the
///     report under the field names of the GraphQL query, and a failed node for a missing, unknown or refused range.
/// </summary>
public class GetNumberRangeGapReportNodeTests : NodeTestBase
{
    private static readonly RtCkId<CkTypeId> InvoiceTypeId = new("Billing/Invoice");

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly ITenantRepository _repository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _session = A.Fake<IOctoSession>();

    public GetNumberRangeGapReportNodeTests()
    {
        A.CallTo(() => _etlContext.TenantRepository).Returns(_repository);
        A.CallTo(() => _etlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).Returns(_session);
    }

    private static NumberRangeGapReport Report()
    {
        var first = new RtEntityId(InvoiceTypeId, new OctoObjectId("5f9b3b3b3b3b3b3b3b3b3b3a"));
        var second = new RtEntityId(InvoiceTypeId, new OctoObjectId("5f9b3b3b3b3b3b3b3b3b3b3b"));
        return new NumberRangeGapReport("InvoiceNumber", 2027, 1, 10, 10,
            [new NumberRangeGap(4, 4, "RE-2027-00004", "RE-2027-00004")],
            [new NumberRangeDuplicate(7, "RE-2027-00007", [first, second])],
            [new NumberRangeIrregularNumber(first, "RE-2027-0001", null)], 1);
    }

    [Fact]
    public async Task WritesTheReportOfTheEngine_ReadWithTheSessionOfTheIdentity()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration
        {
            NumberRange = "InvoiceNumber", FiscalYear = 2027, Identity = NodeExecutionIdentity.ServiceAccount,
            TargetPath = "$.report"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "InvoiceNumber", 2027,
            A<CancellationToken>._)).Returns(Report());
        JsonObject? written = null;
        A.CallTo(() => dataContext.Set("$.report", A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes(call => written = call.GetArgument<JsonObject?>(1));

        await new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _etlContext.GetSessionForAsync(NodeExecutionIdentity.ServiceAccount))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "InvoiceNumber", 2027,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.StartTransaction()).MustNotHaveHappened();
        Assert.NotNull(written);
        Assert.Equal("InvoiceNumber", (string?)written["numberRange"]);
        Assert.Equal(2027, (int?)written["fiscalYear"]);
        Assert.Equal(10, (long?)written["documentCount"]);
        var gap = written["missing"]!.AsArray().Single()!;
        Assert.Equal((4L, 4L, "RE-2027-00004"), ((long)gap["from"]!, (long)gap["to"]!, (string)gap["fromNumber"]!));
        var duplicate = written["duplicates"]!.AsArray().Single()!;
        Assert.Equal("RE-2027-00007", (string?)duplicate["number"]);
        Assert.Equal(["5f9b3b3b3b3b3b3b3b3b3b3a", "5f9b3b3b3b3b3b3b3b3b3b3b"],
            duplicate["documents"]!.AsArray().Select(d => (string)d!["rtId"]!));
        Assert.Equal("Billing/Invoice", (string?)duplicate["documents"]![0]!["ckTypeId"]);
        var irregular = written["irregular"]!.AsArray().Single()!;
        Assert.Null(irregular["sequenceValue"]);
        Assert.Equal(1, (long?)written["irregularCount"]);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task TakesTheRangeAndTheFiscalYearFromTheDataContext_AndDefaultsToTheCaller()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration
        {
            NumberRangePath = "$.range", FiscalYearPath = "$.year", TargetPath = "$.report"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath(dataContext, "$.range", "InvoiceNumber");
        SetupGetSimpleValueByPath<int?>(dataContext, "$.year", 2028);
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "InvoiceNumber", 2028,
            A<CancellationToken>._)).Returns(Report());

        await new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _etlContext.GetSessionForAsync(NodeExecutionIdentity.Caller)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "InvoiceNumber", 2028,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ARangeWithoutPeriods_IsReadWithoutAFiscalYear()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration { NumberRange = "NoteNumber", TargetPath = "$.r" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "NoteNumber", null, A<CancellationToken>._))
            .Returns(new NumberRangeGapReport("NoteNumber", null, 1, 0, 0, [], [], [], 0));

        await new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "NoteNumber", null, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task WithoutARange_FailsBeforeASessionIsOpened()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration { FiscalYear = 2027, TargetPath = "$.r" };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() =>
            new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext));

        A.CallTo(() => _etlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task AnUnknownOrUnreadableRange_FailsTheNode()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration { NumberRange = "Hidden", TargetPath = "$.r" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "Hidden", null, A<CancellationToken>._))
            .Returns((NumberRangeGapReport?)null);

        var exception = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() =>
            new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("'Hidden'", exception.Message, StringComparison.Ordinal);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
            A<TargetValueWriteModes>._)).MustNotHaveHappened();
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task AFiscalYearTheEngineRefuses_FailsTheNodeWithItsMessage()
    {
        var config = new GetNumberRangeGapReportNodeConfiguration { NumberRange = "InvoiceNumber", TargetPath = "$.r" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => _repository.GetNumberRangeGapReportAsync(_session, "InvoiceNumber", null,
                A<CancellationToken>._))
            .ThrowsAsync(new ArgumentException("the report needs the fiscal year.", "fiscalYear"));

        var exception = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() =>
            new GetNumberRangeGapReportNode(next, _etlContext).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Contains("needs the fiscal year", exception.Message, StringComparison.Ordinal);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }
}
