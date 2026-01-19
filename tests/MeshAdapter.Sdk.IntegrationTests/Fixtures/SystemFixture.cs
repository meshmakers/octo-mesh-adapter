using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that initializes the system tenant.
/// </summary>
public class SystemFixture : DatabaseFixture
{
    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // Initialize system tenant
        var systemContext = GetSystemContext();

        // Clean up existing tenant if present
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (i == 0 && await systemContext.IsSystemTenantExistingAsync())
                {
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    await Task.Delay(1000);
                    continue;
                }

                break;
            }
            catch (TenantException)
            {
                // Ignore tenant exceptions during cleanup
            }
        }

        await systemContext.CreateSystemTenantAsync();
    }
}
