namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that provides the CK model for testing.
/// The System CK model is automatically imported when the system tenant is created.
/// </summary>
public class ImportCkModelFixture : SystemFixture
{
    /// <summary>
    /// Clears the tenant collections.
    /// </summary>
    public async Task ClearCollectionAsync()
    {
        var systemContext = GetSystemContext();
        await systemContext.ClearSystemTenantAsync();
    }
}
