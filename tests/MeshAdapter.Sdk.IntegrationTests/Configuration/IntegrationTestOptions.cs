namespace MeshAdapter.Sdk.IntegrationTests.Configuration;

/// <summary>
/// Configuration options for integration tests.
/// </summary>
public class IntegrationTestOptions
{
    /// <summary>
    /// MongoDB Docker image to use for test containers.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Pinned to an exact patch version, never the floating <c>mongo:8.0</c> tag.</b> This repo
    /// was the last one in the estate still floating, and on 2026-09-16 that tag moved to 8.0.28,
    /// which refuses to start on any Linux kernel &gt;= 6.19 (SERVER-121912) — Docker Desktop's VM
    /// reached 7.0.12-linuxkit. Every fixture then failed inside
    /// <c>MongoDbBuilder.InitiateReplicaSetAsync</c> with a Docker "container is not running"
    /// conflict naming neither MongoDB nor the kernel, so the whole suite died at once and read like
    /// broken fixture code. 8.0.15 is what the other integration-test projects in the estate pin and
    /// what the CI lanes pre-pull. Override per machine with
    /// <c>OCTO_INTEGRATIONTEST__MONGODBIMAGE</c>; see <see cref="IntegrationTestConfiguration" />.
    /// </remarks>
    public string MongoDbImage { get; set; } = "mongo:8.0.15";

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
