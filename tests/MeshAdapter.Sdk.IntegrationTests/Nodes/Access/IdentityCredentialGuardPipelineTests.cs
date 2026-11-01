using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using F = MeshAdapter.Sdk.IntegrationTests.Fixtures.IdentityCredentialPipelineFixture;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Access;

/// <summary>
///     Credential guard regression tests for pipelines, against the real <c>System.Identity</c> model: extract nodes never
///     emit the user's <c>PasswordHash</c>, <c>SecurityStamp</c> or <c>UserTokens</c>, refuse a filter or sort on them, and
///     load nodes refuse writes of those Hidden attributes (also in a method run) and of the MethodOnly / ReadOnly identity
///     attributes outside the method run. Real orchestrator, real nodes, real CK cache, MongoDB. The seeded user holds known
///     dummy values (<see cref="F.DummyValues" />); no pipeline output, error or captured log line may contain them. Since
///     AB#8059 the load nodes also refuse the identity-owned User attributes and the identity association edges (insert and
///     delete) outside the run of an identity method (the engine's <c>IdentityOwnedWrites</c> list). The
///     guard rules themselves are covered with a test model in <c>MeshAdapter.Sdk.Tests/Nodes/Access</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class IdentityCredentialGuardPipelineTests(F fixture) : IClassFixture<F>
{
    private static readonly OctoObjectId PipelineId = new("000000000000000000007781");

    private static readonly string[] HiddenAttributes = ["PasswordHash", "SecurityStamp", "UserTokens"];

    // ------------------------------------------------------------------------------------------------- extract

    [Fact]
    public async Task GetRtEntitiesByType_EmitsTheUser_WithoutCredentials()
    {
        var output = await RunOkAsync("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: System.Identity/User
                targetPath: $.users
            """);

        output.Should().Contain(F.ProbeUserName, "positive control: the user is emitted");
        AssertNoCredential(output);
    }

    [Fact]
    public async Task GetRtEntitiesById_EmitsTheUser_WithoutCredentials()
    {
        var output = await RunOkAsync($"""
            transformations:
              - type: GetRtEntitiesById@1
                ckTypeId: System.Identity/User
                rtIds: ['{fixture.ProbeUserRtId}']
                targetPath: $.users
            """);

        output.Should().Contain(F.ProbeUserName, "positive control");
        AssertNoCredential(output);
    }

    [Fact]
    public async Task GetAssociationTargets_FromTheRole_EmitsTheUser_WithoutCredentials()
    {
        var output = await RunOkAsync($"""
            transformations:
              - type: GetAssociationTargets@1
                originRtId: '{fixture.ProbeRoleRtId}'
                originCkTypeId: System.Identity/Role
                targetCkTypeId: System.Identity/User
                associationRoleId: System.Identity/AssignedRole
                graphDirection: Inbound
                targetPath: $.users
            """);

        output.Should().Contain(F.ProbeUserName, "positive control: the association is followed");
        AssertNoCredential(output);
    }

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("SecurityStamp")]
    [InlineData("UserTokens.Value")]
    public async Task GetRtEntitiesByType_FilterOnACredential_IsRefused(string path)
    {
        await AssertRefusedAsync($"""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: System.Identity/User
                targetPath: $.users
                fieldFilters:
                  - attributePath: {path}
                    operator: Equals
                    comparisonValue: {F.DummyPasswordHash}
            """, "RT_ATTRIBUTE_HIDDEN");
    }

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("SecurityStamp")]
    public async Task GetRtEntitiesByType_SortOnACredential_IsRefused(string path)
    {
        await AssertRefusedAsync($"""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: System.Identity/User
                targetPath: $.users
                sortOrders:
                  - attributeName: {path}
                    sortOrder: Ascending
            """, "RT_ATTRIBUTE_HIDDEN");
    }

    [Fact]
    public async Task GetRtEntitiesById_FilterOnACredential_IsRefused()
    {
        await AssertRefusedAsync($"""
            transformations:
              - type: GetRtEntitiesById@1
                ckTypeId: System.Identity/User
                rtIds: ['{fixture.ProbeUserRtId}']
                targetPath: $.users
                fieldFilters:
                  - attributePath: PasswordHash
                    operator: Equals
                    comparisonValue: {F.DummyPasswordHash}
            """, "RT_ATTRIBUTE_HIDDEN");
    }

    [Fact]
    public async Task GetAssociationTargets_FilterOnACredential_IsRefused()
    {
        await AssertRefusedAsync($"""
            transformations:
              - type: GetAssociationTargets@1
                originRtId: '{fixture.ProbeRoleRtId}'
                originCkTypeId: System.Identity/Role
                targetCkTypeId: System.Identity/User
                associationRoleId: System.Identity/AssignedRole
                graphDirection: Inbound
                targetPath: $.users
                fieldFilters:
                  - attributePath: SecurityStamp
                    operator: Equals
                    comparisonValue: {F.DummySecurityStamp}
            """, "RT_ATTRIBUTE_HIDDEN");
    }

    [Fact]
    public async Task GetQueryById_WithACredentialColumnInTheStoredQuery_ReturnsNoCredential()
    {
        // A stored query can carry a Hidden column (written by an import or any non-GraphQL writer). Running it in a
        // pipeline must neither return the value nor fail open.
        var queryRtId = await fixture.CreateUserQueryAsync(["UserName", "PasswordHash", "SecurityStamp"]);
        var logStart = fixture.LogCount;

        var (output, failure) = await RunAsync($"""
            transformations:
              - type: GetQueryById@1
                queryRtId: '{queryRtId}'
                targetPath: $.result
            """);

        AssertNoCredential(output ?? string.Empty);
        AssertNoCredential(failure?.ToString() ?? string.Empty);
        fixture.LogLinesWithDummyValuesSince(logStart).Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueryById_WithACredentialFilterInTheStoredQuery_IsRefused()
    {
        var queryRtId = await fixture.CreateUserQueryAsync(["UserName"], ("PasswordHash", F.DummyPasswordHash));

        await AssertRefusedAsync($"""
            transformations:
              - type: GetQueryById@1
                queryRtId: '{queryRtId}'
                targetPath: $.result
            """, "RT_ATTRIBUTE_HIDDEN");
    }

    // ------------------------------------------------------------------------------------------------- load

    public static TheoryData<string, string> RestrictedUpdates() => new()
    {
        { "PasswordHash", F.DummyPasswordHash },
        { "SecurityStamp", F.DummySecurityStamp },
        { "UserName", "renamed-by-pipeline" },
        { "Email", "redirected@example.invalid" },
        { "AccessFailedCount", "0" },
        { "LockoutEnd", "2020-01-01T00:00:00Z" }
    };

    [Theory]
    [MemberData(nameof(RestrictedUpdates))]
    public async Task ApplyChanges2_UpdateOfARestrictedUserAttribute_IsRefused_AndNothingChanges(string attribute,
        string value)
    {
        await AssertRefusedAsync(UpdatePipeline("ApplyChanges@2", attribute), "RT_ATTRIBUTE_WRITE_RESTRICTED",
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = value });

        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [InlineData("PasswordHash", F.DummyPasswordHash)]
    [InlineData("UserName", "renamed-by-pipeline")]
    public async Task ApplyChanges1_UpdateOfARestrictedUserAttribute_IsRefused_AndNothingChanges(string attribute,
        string value)
    {
        await AssertRefusedAsync(UpdatePipeline("ApplyChanges@1", attribute), "RT_ATTRIBUTE_WRITE_RESTRICTED",
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = value });

        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [InlineData("PasswordHash", F.DummyPasswordHash)]
    [InlineData("UserName", "inserted-by-pipeline")]
    public async Task ApplyChanges2_InsertOfAUser_WithARestrictedAttribute_IsRefused(string attribute, string value)
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();

        await AssertRefusedAsync($"""
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._inserts
                targetValueKind: Array
                updateKind: INSERT
                ckTypeId: System.Identity/User
                rtIdPath: $.rtId
                attributeUpdates:
                  - attributeName: FirstName
                    attributeValueType: String
                    value: inserted
                  - attributeName: {attribute}
                    attributeValueType: String
                    valuePath: $.value
              - type: ApplyChanges@2
                entityUpdatesPath: $._inserts
            """, "RT_ATTRIBUTE_WRITE_RESTRICTED", new JsonObject { ["rtId"] = rtId, ["value"] = value });

        (await UserExistsAsync(rtId)).Should().BeFalse("nothing is written");
    }

    [Theory]
    [InlineData("PasswordHash", F.DummyPasswordHash)]
    [InlineData("SecurityStamp", F.DummySecurityStamp)]
    public async Task ApplyChanges2_InTheRunOfAnIdentityMethod_StillRefusesAHiddenWrite(string attribute, string value)
    {
        var marker = await MarkerAsync("SetRoles-1");

        await AssertRefusedAsync(UpdatePipeline("ApplyChanges@2", attribute), "RT_ATTRIBUTE_WRITE_RESTRICTED",
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = value }, marker);

        await AssertProbeUserUnchangedAsync();
    }

    [Fact]
    public async Task ApplyChanges2_UpdateOfAVisibleUserAttribute_IsApplied_PositiveControl()
    {
        var firstName = "first-" + Guid.NewGuid().ToString("N")[..8];

        await RunOkAsync(UpdatePipeline("ApplyChanges@2", "FirstName"),
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = firstName });

        (await RawAsync("FirstName")).Should().Be(firstName);
        await AssertProbeUserUnchangedAsync();
    }

    // ------------------------------------------------------------------------------------- identity-owned (AB#8059)

    // The engine's IdentityOwnedWrites list: User attributes only the identity service writes (its REST API and methods).
    public static TheoryData<string, string> IdentityOwnedUpdates() => new()
    {
        // attribute, pipeline value type
        { "NormalizedUserName", "String" },
        { "NormalizedEmail", "String" },
        { "EmailConfirmed", "Boolean" },
        { "PhoneNumberConfirmed", "Boolean" },
        { "TwoFactorEnabled", "Boolean" },
        { "LockoutEnabled", "Boolean" },
        { "ResetPasswordOnLogin", "Boolean" },
        { "Claims", "RecordArray" },
        { "UserLogins", "RecordArray" },
        { "PreferredChannelBindingId", "String" }
    };

    [Theory]
    [MemberData(nameof(IdentityOwnedUpdates))]
    public async Task ApplyChanges2_UpdateOfAnIdentityOwnedUserAttribute_IsRefused_AndNothingChanges(string attribute,
        string valueType)
    {
        await AssertRefusedAsync(UpdatePipeline("ApplyChanges@2", attribute, valueType), "IDENTITY_OWNED",
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = SampleValue(valueType) });

        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [InlineData("TwoFactorEnabled", "Boolean")]
    [InlineData("Claims", "RecordArray")]
    public async Task ApplyChanges1_UpdateOfAnIdentityOwnedUserAttribute_IsRefused_AndNothingChanges(string attribute,
        string valueType)
    {
        await AssertRefusedAsync(UpdatePipeline("ApplyChanges@1", attribute, valueType), "IDENTITY_OWNED",
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = SampleValue(valueType) });

        await AssertProbeUserUnchangedAsync();
    }

    [Theory]
    [MemberData(nameof(IdentityOwnedUpdates))]
    public async Task ApplyChanges2_InsertOfAUser_WithAnIdentityOwnedAttribute_IsRefused(string attribute, string valueType)
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();

        await AssertRefusedAsync($"""
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._inserts
                targetValueKind: Array
                updateKind: INSERT
                ckTypeId: System.Identity/User
                rtIdPath: $.rtId
                attributeUpdates:
                  - attributeName: FirstName
                    attributeValueType: String
                    value: inserted
                  - attributeName: {attribute}
                    attributeValueType: {valueType}
                    valuePath: $.value
              - type: ApplyChanges@2
                entityUpdatesPath: $._inserts
            """, new JsonObject { ["rtId"] = rtId, ["value"] = SampleValue(valueType) }, null,
            // Once System.Identity/User replaces the generic create (User.Create), the replaced-create guard refuses the
            // insert first; either refusal keeps the identity-owned value out.
            "IDENTITY_OWNED", "GENERIC_CREATE_REPLACED_BY_METHOD");

        (await UserExistsAsync(rtId)).Should().BeFalse("nothing is written");
    }

    public static TheoryData<string, string> IdentityAssociationChanges()
    {
        var data = new TheoryData<string, string>();
        foreach (var role in new[]
                 {
                     "System.Identity/AssignedRole", "System.Identity/GroupMember", "System.Identity/ChildGroup",
                     "System.Identity/GrantsPermission", "System.Identity/MayActAs"
                 })
        {
            data.Add(role, "Create");
            data.Add(role, "Delete");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(IdentityAssociationChanges))]
    public async Task ApplyChanges2_IdentityAssociationChange_IsRefused_AndTheRoleAssignmentStays(string roleId,
        string updateKind)
    {
        // The guard judges the role, not the end types; the seeded user -> role edge is the one a delete would remove.
        await AssertRefusedAsync($"""
            transformations:
              - type: CreateAssociationUpdate@1
                targetPath: $._associations
                targetValueKind: Array
                updateKind: {updateKind}
                originRtId: '{fixture.ProbeUserRtId}'
                originCkTypeId: System.Identity/User
                targetRtId: '{fixture.ProbeRoleRtId}'
                targetCkTypeId: System.Identity/Role
                associationRoleId: {roleId}
              - type: ApplyChanges@2
                associationUpdatesPath: $._associations
            """, "IDENTITY_OWNED");

        await AssertRoleAssignmentStaysAsync();
    }

    [Fact]
    public async Task ApplyChanges2_RoleAssignmentDelete_FromTheRoleSide_IsRefused()
    {
        await AssertRefusedAsync($"""
            transformations:
              - type: CreateAssociationUpdate@1
                targetPath: $._associations
                targetValueKind: Array
                updateKind: Delete
                originRtId: '{fixture.ProbeRoleRtId}'
                originCkTypeId: System.Identity/Role
                targetRtId: '{fixture.ProbeUserRtId}'
                targetCkTypeId: System.Identity/User
                associationRoleId: System.Identity-2.30.0/AssignedRole
              - type: ApplyChanges@2
                associationUpdatesPath: $._associations
            """, "IDENTITY_OWNED");

        await AssertRoleAssignmentStaysAsync();
    }

    [Fact]
    public async Task ApplyChanges2_IdentityAssociation_IsAllowed_InTheRunOfAnIdentityMethod_AndTheEdgeIsWritten()
    {
        // The exemption: a pipeline that implements a System.Identity method on the User (marker of the running
        // pipeline) may write the identity associations. Insert a second role edge and remove it again.
        var marker = await MarkerAsync("SetRoles-1");
        var roleRtId = await fixture.CreateRoleAsync("ProbeRole-" + Guid.NewGuid().ToString("N")[..6]);

        string Change(string kind) => $"""
            transformations:
              - type: CreateAssociationUpdate@1
                targetPath: $._associations
                targetValueKind: Array
                updateKind: {kind}
                originRtId: '{fixture.ProbeUserRtId}'
                originCkTypeId: System.Identity/User
                targetRtId: '{roleRtId}'
                targetCkTypeId: System.Identity/Role
                associationRoleId: System.Identity/AssignedRole
              - type: ApplyChanges@2
                associationUpdatesPath: $._associations
            """;

        var (_, insertFailure) = await RunAsync(Change("Create"), marker: marker);
        insertFailure.Should().BeNull();
        (await RoleUsersAsync(roleRtId)).Should().Contain(F.ProbeUserName);

        var (_, deleteFailure) = await RunAsync(Change("Delete"), marker: marker);
        deleteFailure.Should().BeNull();
        (await RoleUsersAsync(roleRtId)).Should().NotContain(F.ProbeUserName);
    }

    [Fact]
    public async Task ApplyChanges2_IdentityOwnedAttribute_IsAllowed_InTheRunOfAnIdentityMethod()
    {
        var marker = await MarkerAsync("SetRoles-1");
        var before = await RawAsync("PhoneNumberConfirmed");

        var (_, failure) = await RunAsync(UpdatePipeline("ApplyChanges@2", "PhoneNumberConfirmed", "Boolean"),
            new JsonObject { ["rtId"] = fixture.ProbeUserRtId, ["value"] = false }, marker);

        failure.Should().BeNull();
        (await RawAsync("PhoneNumberConfirmed")).Should().Be(before ?? false);
    }

    private static JsonNode SampleValue(string valueType) => valueType switch
    {
        "Boolean" => JsonValue.Create(true),
        "RecordArray" => new JsonArray(),
        _ => JsonValue.Create("SET-BY-PIPELINE-8059")
    };

    private async Task<string> RoleUsersAsync(string roleRtId) => await RunOkAsync($"""
        transformations:
          - type: GetAssociationTargets@1
            originRtId: '{roleRtId}'
            originCkTypeId: System.Identity/Role
            targetCkTypeId: System.Identity/User
            associationRoleId: System.Identity/AssignedRole
            graphDirection: Inbound
            targetPath: $.users
        """);

    private async Task AssertRoleAssignmentStaysAsync() =>
        (await RoleUsersAsync(fixture.ProbeRoleRtId)).Should().Contain(F.ProbeUserName,
            "the seeded role assignment is not removed");

    // ------------------------------------------------------------------------------------------------- helpers

    private static string UpdatePipeline(string applyNode, string attribute, string? valueType = null)
    {
        valueType ??= attribute switch
        {
            "AccessFailedCount" => "Int",
            "LockoutEnd" => "DateTime",
            _ => "String"
        };
        var applyConfig = applyNode == "ApplyChanges@1" ? "path: $._updates" : "entityUpdatesPath: $._updates";
        return $"""
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._updates
                targetValueKind: Array
                updateKind: UPDATE
                ckTypeId: System.Identity/User
                rtIdPath: $.rtId
                attributeUpdates:
                  - attributeName: {attribute}
                    attributeValueType: {valueType}
                    valuePath: $.value
              - type: {applyNode}
                {applyConfig}
            """;
    }

    private async Task<ExecutePipelineMethodInvocation> MarkerAsync(string methodId)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var cache = fixture.GetService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(cache);
        var type = cache.GetRtCkType(repository.TenantId, new RtCkId<CkTypeId>(F.UserCkTypeId));
        var method = type.AllMethods.Values.Single(m => m.QualifiedMethodId.EndsWith("." + methodId, StringComparison.Ordinal));
        return new ExecutePipelineMethodInvocation
        {
            MethodId = method.QualifiedMethodId,
            InvocationId = Guid.NewGuid(),
            CkTypeId = F.UserCkTypeId,
            PipelineRtId = PipelineId.ToString()
        };
    }

    private Task AssertRefusedAsync(string yaml, string code, JsonObject? input = null,
        ExecutePipelineMethodInvocation? marker = null) => AssertRefusedAsync(yaml, input, marker, code);

    private async Task AssertRefusedAsync(string yaml, JsonObject? input, ExecutePipelineMethodInvocation? marker,
        params string[] anyOfCodes)
    {
        var logStart = fixture.LogCount;
        var (output, failure) = await RunAsync(yaml, input, marker);

        failure.Should().NotBeNull($"the pipeline must be refused, but returned {output}");
        var text = failure!.ToString();
        anyOfCodes.Should().Contain(code => text.Contains(code), $"the refusal names one of {string.Join(", ", anyOfCodes)}");
        AssertNoCredential(text);
        fixture.LogLinesWithDummyValuesSince(logStart).Should().BeEmpty("no log line may carry a credential");
    }

    private async Task<string> RunOkAsync(string yaml, JsonObject? input = null)
    {
        var logStart = fixture.LogCount;
        var (output, failure) = await RunAsync(yaml, input);
        failure.Should().BeNull();
        fixture.LogLinesWithDummyValuesSince(logStart).Should().BeEmpty("no log line may carry a credential");
        return output!;
    }

    private async Task<(string? Output, Exception? Failure)> RunAsync(string yaml, JsonObject? input = null,
        ExecutePipelineMethodInvocation? marker = null)
    {
        fixture.EnsureInitialized();
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, PipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", PipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), methodInvocation: marker);

        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        try
        {
            var result = await fixture.GetService<IEtlDataOrchestrator>()
                .ExecutePipelineAsync(definition, etlContext, value: input ?? new JsonObject());
            var node = result as JsonNode ?? JsonSerializer.SerializeToNode(result);
            return (node?.ToJsonString() ?? string.Empty, null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    private static void AssertNoCredential(string text)
    {
        foreach (var dummy in F.DummyValues)
        {
            text.Should().NotContain(dummy);
        }

        foreach (var hidden in HiddenAttributes)
        {
            text.Should().NotContain($"\"{hidden}\"", "a Hidden attribute is not emitted, not even as a key");
        }
    }

    private async Task<RtEntity?> ReadUserAsync(string rtId)
    {
        // The repository does not filter by access: this is the stored state, read the way the identity store reads it.
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        return await repository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(new RtCkId<CkTypeId>(F.UserCkTypeId), new OctoObjectId(rtId)));
    }

    private async Task<object?> RawAsync(string attribute)
    {
        var user = await ReadUserAsync(fixture.ProbeUserRtId);
        user.Should().NotBeNull();
        return user!.Attributes.TryGetValue(attribute, out var value) ? value : null;
    }

    private async Task AssertProbeUserUnchangedAsync()
    {
        (await RawAsync("PasswordHash")).Should().Be(F.DummyPasswordHash);
        (await RawAsync("SecurityStamp")).Should().Be(F.DummySecurityStamp);
        (await RawAsync("UserName")).Should().Be(F.ProbeUserName);
        (await RawAsync("Email")).Should().Be(F.ProbeUserEmail);
        Convert.ToInt32(await RawAsync("AccessFailedCount")).Should().Be(3);
        (await RawAsync("LockoutEnd")).Should().BeNull("the seed does not set LockoutEnd");
        // AB#8059: the identity-owned fields keep their seeded values.
        (await RawAsync("NormalizedUserName")).Should().Be(F.ProbeUserName.ToUpperInvariant());
        (await RawAsync("NormalizedEmail")).Should().Be(F.ProbeUserEmail.ToUpperInvariant());
        (await RawAsync("EmailConfirmed")).Should().Be(true);
        (await RawAsync("PhoneNumberConfirmed")).Should().Be(false);
        (await RawAsync("TwoFactorEnabled")).Should().Be(false);
        (await RawAsync("LockoutEnabled")).Should().Be(true);
        (await RawAsync("ResetPasswordOnLogin")).Should().Be(false);
        (await RawAsync("Claims")).Should().BeNull("the seed does not set Claims");
        (await RawAsync("UserLogins")).Should().BeNull("the seed does not set UserLogins");
        (await RawAsync("PreferredChannelBindingId")).Should().BeNull();
        ((System.Collections.IEnumerable)(await RawAsync("UserTokens"))!).Cast<object>().Should().HaveCount(1);
    }

    private async Task<bool> UserExistsAsync(string rtId) => await ReadUserAsync(rtId) != null;
}
