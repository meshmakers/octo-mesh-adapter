using System.Security.Cryptography;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Secrets;

/// <summary>
/// AB#5538 test support: a real engine protector over a GENERATED key ring (never a real key) and a
/// fake CK cache with a type that carries Secret attributes.
/// </summary>
internal static class SecretTestSupport
{
    public const string TenantId = "secret-tenant";
    public static readonly RtCkId<CkTypeId> CkTypeId = new("SecretTest/Credentials");
    public static readonly RtCkId<CkRecordId> SettingsRecordId = new("SecretTest/Settings");

    /// <summary>Generates a fresh random 256-bit key, base64 - for tests only.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The engine's protector as AddRuntimeEngine() registers it, bound from configuration exactly the
    /// way the adapter host binds it (section SecretEncryption).
    /// </summary>
    public static ISecretAttributeProtector CreateProtector(IReadOnlyDictionary<string, string>? keys,
        string? activeKeyId = null)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (kid, key) in keys ?? new Dictionary<string, string>())
        {
            settings[$"SecretEncryption:Keys:{kid}"] = key;
        }

        if (activeKeyId != null)
        {
            settings["SecretEncryption:ActiveKeyId"] = activeKeyId;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddRuntimeEngine();
        return services.BuildServiceProvider().GetRequiredService<ISecretAttributeProtector>();
    }

    public static ISecretAttributeProtector CreateProtector(string keyId = "k1") =>
        CreateProtector(new Dictionary<string, string> { [keyId] = GenerateKey() }, keyId);

    private static CkTypeAttributeGraph Attribute(string modelName, string name, AttributeValueTypesDto type,
        CkId<CkRecordId>? recordId = null) =>
        new(ckAttributeId: new CkId<CkAttributeId>(modelName, new CkAttributeId(name)),
            attributeName: name,
            autoCompleteValues: null,
            valueType: type,
            valueCkRecordId: recordId,
            valueCkEnumId: null,
            autoIncrementReference: null,
            metaData: null,
            defaultValues: null,
            isOptional: true,
            description: null);

    /// <summary>
    /// Fake cache: <see cref="CkTypeId" /> with <c>Name</c> (String), <c>Password</c> (Secret),
    /// <c>Settings</c> (Record of <see cref="SettingsRecordId" />: <c>Url</c> String, <c>ApiKey</c> Secret)
    /// and <c>Endpoints</c> (RecordArray of the same record).
    /// </summary>
    public static ICkCacheService CreateCkCache()
    {
        var recordCkId = new CkId<CkRecordId>("SecretTest", new CkRecordId("Settings"));
        var recordAttributes = new[]
        {
            Attribute("SecretTest", "Url", AttributeValueTypesDto.String),
            Attribute("SecretTest", "ApiKey", AttributeValueTypesDto.Secret)
        }.ToDictionary(a => a.CkAttributeId, a => a);
        var recordGraph = new CkRecordGraph(recordCkId, isAbstract: false, isFinal: false, baseRecords: [],
            derivedFromCkRecordId: null, derivedRecords: [], definedAttributes: [], allAttributes: recordAttributes,
            description: string.Empty);

        var typeAttributes = new[]
        {
            Attribute("SecretTest", "Name", AttributeValueTypesDto.String),
            Attribute("SecretTest", "Password", AttributeValueTypesDto.Secret),
            Attribute("SecretTest", "Settings", AttributeValueTypesDto.Record, recordCkId),
            Attribute("SecretTest", "Endpoints", AttributeValueTypesDto.RecordArray, recordCkId)
        }.ToDictionary(a => a.CkAttributeId, a => a);
        var typeGraph = new CkTypeGraph(
            ckTypeId: new CkId<CkTypeId>("SecretTest", new CkTypeId("Credentials")),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: typeAttributes,
            indexes: [],
            associations: new CkGraphDirectedAssociations([]),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out typeGraph!))
            .Returns(true).AssignsOutAndRefParameters(typeGraph);
        A.CallTo(() => cache.TryGetRtCkRecord(TenantId, A<RtCkId<CkRecordId>>._, out recordGraph!))
            .Returns(true).AssignsOutAndRefParameters(recordGraph);
        A.CallTo(() => cache.TryGetCkRecord(TenantId, A<CkId<CkRecordId>>._, out recordGraph!))
            .Returns(true).AssignsOutAndRefParameters(recordGraph);
        return cache;
    }
}
