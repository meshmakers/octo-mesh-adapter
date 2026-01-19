namespace MeshAdapter.Sdk.IntegrationTests.Configuration;

/// <summary>
/// Configuration options for integration tests.
/// </summary>
public class IntegrationTestOptions
{
    /// <summary>
    /// MongoDB Docker image to use for test containers.
    /// </summary>
    public string MongoDbImage { get; set; } = "mongo:7.0";

    /// <summary>
    /// Whether to use a local MongoDB instance instead of Testcontainers.
    /// </summary>
    public bool UseLocalDatabase { get; set; }

    /// <summary>
    /// Local MongoDB host (when UseLocalDatabase is true).
    /// </summary>
    public string LocalDatabaseHost { get; set; } = "localhost:27017";

    /// <summary>
    /// Admin user for MongoDB authentication.
    /// </summary>
    public string AdminUser { get; set; } = "admin";

    /// <summary>
    /// Admin user password for MongoDB authentication.
    /// </summary>
    public string AdminUserPassword { get; set; } = "admin";

    /// <summary>
    /// Database user password.
    /// </summary>
    public string DatabaseUserPassword { get; set; } = "testPassword123!";

    /// <summary>
    /// Whether to use direct connection for local database.
    /// </summary>
    public bool UseDirectConnection { get; set; } = true;
}
