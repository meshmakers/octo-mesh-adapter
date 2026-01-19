namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that provides the test environment with CK model imported.
/// For now, we skip importing sample data since the types may vary by CK model version.
/// Tests should create data directly via the repository if needed.
/// </summary>
public class SampleDataFixture : ImportCkModelFixture
{
    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // Note: Sample data import is skipped for now because:
        // 1. The available types depend on the CK model version
        // 2. Tests can create needed entities directly via the repository
        //
        // To add sample data import later:
        // var importRtModelCommand = GetService<IImportRtModelCommand>();
        // var systemContext = GetSystemContext();
        // var repository = systemContext.GetSystemTenantRepository();
        // await importRtModelCommand.ImportAsync(repository, "testData/sampleQueryData.yaml", ...);
    }
}
