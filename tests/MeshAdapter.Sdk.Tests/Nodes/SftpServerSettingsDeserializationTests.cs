using System.Text.Json;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Covers the step every other SFTP test skips: turning the stored GlobalConfiguration payload
/// into <see cref="SftpServerSettings" />. The node tests hand the resolver an
/// already-constructed record, so none of them ever sees the JSON a tenant actually stores -
/// which is where the settings failed in the field.
/// </summary>
public class SftpServerSettingsDeserializationTests
{
    /// <summary>
    /// The exact call <c>IGlobalConfiguration.GetValue&lt;T&gt;</c> makes on the stored payload.
    /// Going through it rather than through <see cref="JsonSerializer" /> directly keeps the
    /// test bound to the serializer options the adapter really runs with.
    /// </summary>
    private static SftpServerSettings Deserialize(string payload)
    {
        return payload.Deserialize<SftpServerSettings>();
    }

    /// <summary>
    /// Builds the payload shape a tenant entry has: every attribute the CK type declares is a
    /// key, and an optional attribute that was never given a value carries null rather than
    /// being left out.
    /// </summary>
    private static string Payload(string maxConcurrentConnections)
    {
        return $$"""
                 {
                   "host": "sftp.example.com",
                   "port": 22,
                   "username": "user",
                   "password": "secret",
                   "privateKey": null,
                   "privateKeyPassphrase": null,
                   "maxConcurrentConnections": {{maxConcurrentConnections}}
                 }
                 """;
    }

    [Fact]
    public void Deserialize_MaxConcurrentConnectionsNull_UsesDeclaredDefault()
    {
        // System.Communication/SftpConfiguration declares MaxConcurrentConnections optional, and
        // an optional CK attribute without a value is serialized as a present key holding null.
        // A non-nullable int keeps its C# initializer only when the key is absent, so this
        // payload failed the whole pipeline with "The JSON value could not be converted to
        // System.Int32. Path: $.maxConcurrentConnections" before any node did any work.
        var settings = Deserialize(Payload("null"));

        Assert.Equal(3, settings.MaxConcurrentConnections);
    }

    [Fact]
    public void Deserialize_MaxConcurrentConnectionsMissing_UsesDeclaredDefault()
    {
        var payload = """
                      {
                        "host": "sftp.example.com",
                        "port": 22,
                        "username": "user",
                        "password": "secret"
                      }
                      """;

        var settings = Deserialize(payload);

        // "Key absent" and "key present but null" both mean "not configured" and must therefore
        // produce the same settings - that equivalence is the actual fix.
        Assert.Equal(3, settings.MaxConcurrentConnections);
    }

    [Fact]
    public void Deserialize_MaxConcurrentConnectionsSet_KeepsConfiguredValue()
    {
        var settings = Deserialize(Payload("5"));

        // Tolerating null must not swallow a value an operator did configure.
        Assert.Equal(5, settings.MaxConcurrentConnections);
    }

    [Fact]
    public void Deserialize_EveryIntNull_UsesDeclaredDefaults()
    {
        var payload = """
                      {
                        "host": "sftp.example.com",
                        "port": null,
                        "username": "user",
                        "password": "secret",
                        "maxConcurrentConnections": null,
                        "connectTimeoutSeconds": null,
                        "operationTimeoutSeconds": null,
                        "waitForSlotTimeoutSeconds": null
                      }
                      """;

        var settings = Deserialize(payload);

        // Only MaxConcurrentConnections is an optional attribute of the CK type today. The other
        // numbers carry the same trap the moment the CK type gains them, so they are covered
        // here rather than after the next field fails in a tenant.
        Assert.Equal(22, settings.Port);
        Assert.Equal(3, settings.MaxConcurrentConnections);
        Assert.Equal(0, settings.ConnectTimeoutSeconds);
        Assert.Equal(0, settings.OperationTimeoutSeconds);
        Assert.Equal(0, settings.WaitForSlotTimeoutSeconds);
    }

    [Fact]
    public void Deserialize_MaxConcurrentConnectionsNotANumber_StillFails()
    {
        // Null is the one token that means "not configured". Anything else is a broken entry and
        // has to keep failing loudly instead of quietly resolving to the default.
        Assert.Throws<JsonException>(() => Deserialize(Payload("\"three\"")));
    }
}
