# Octo Mesh Adapter - Integration Test Concept

Dieses Dokument beschreibt das Konzept für Integrationstests im Mesh Adapter, mit besonderem Fokus auf Extract-Nodes wie `GetQueryByIdNode`.

## Inhaltsverzeichnis

1. [Übersicht](#übersicht)
2. [Architektur](#architektur)
3. [Infrastruktur](#infrastruktur)
4. [Projektstruktur](#projektstruktur)
5. [Test-Fixtures](#test-fixtures)
6. [Testdaten-Management](#testdaten-management)
7. [Beispiel: GetQueryByIdNode](#beispiel-getquerybyidnode)
8. [CI/CD Integration](#cicd-integration)

---

## Übersicht

### Unterschied Unit Tests vs. Integration Tests

| Aspekt | Unit Tests | Integration Tests |
|--------|------------|-------------------|
| Scope | Einzelne Node-Logik | Node + reale Abhängigkeiten |
| Datenbank | Gemockt (FakeItEasy) | Echte MongoDB (Testcontainers) |
| Geschwindigkeit | Schnell (~1ms) | Langsamer (~100-500ms) |
| Isolation | Vollständig isoliert | Integriert mit Infrastruktur |
| Zweck | Logik-Validierung | End-to-End Datenfluss |

### Wann Integration Tests?

Integration Tests sind erforderlich für:

- **Extract Nodes**: Datenbank-Abfragen mit echten Queries
- **Load Nodes**: Persistierung mit Transaction-Handling
- **Multi-Node Pipelines**: Datenfluss über mehrere Nodes
- **CK Cache Interaktionen**: Construction Kit Metadaten

---

## Architektur

### Komponentendiagramm

```
┌─────────────────────────────────────────────────────────────────┐
│                    Integration Test                              │
├─────────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │ TestFixture  │  │ TestData     │  │ Node Under Test      │  │
│  │ (IAsyncLife) │  │ Builder      │  │ (GetQueryByIdNode)   │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────┬───────────┘  │
│         │                 │                      │              │
│         ▼                 ▼                      ▼              │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Real Service Instances                       │  │
│  │  ┌─────────────────┐  ┌─────────────────────────────┐    │  │
│  │  │ ITenantRepository│  │ ICkCacheService             │    │  │
│  │  │ (Real Impl)      │  │ (In-Memory or Real)         │    │  │
│  │  └────────┬─────────┘  └─────────────────────────────┘    │  │
│  └───────────┼───────────────────────────────────────────────┘  │
│              │                                                   │
│              ▼                                                   │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Testcontainers                               │  │
│  │  ┌─────────────────┐  ┌─────────────────────────────┐    │  │
│  │  │ MongoDB         │  │ (Optional) CrateDB          │    │  │
│  │  │ Container       │  │ Container                   │    │  │
│  │  └─────────────────┘  └─────────────────────────────┘    │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Abhängigkeiten für GetQueryByIdNode

```
GetQueryByIdNode
    ├── IMeshEtlContext
    │       └── ITenantRepository
    │               ├── ISession (Transaction Management)
    │               ├── GetRtEntityByRtIdAsync<T>()
    │               └── GetRtEntitiesGraphByTypeAsync()
    └── ICkCacheService
            └── CK Type Definitions
```

---

## Infrastruktur

### Testcontainers für MongoDB

**NuGet Pakete:**

```xml
<ItemGroup>
    <PackageReference Include="Testcontainers" Version="4.4.0" />
    <PackageReference Include="Testcontainers.MongoDb" Version="4.4.0" />
    <PackageReference Include="FluentAssertions" Version="8.0.0" />
</ItemGroup>
```

### MongoDB Container Setup

```csharp
public class MongoDbFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;

    public string ConnectionString { get; private set; } = null!;
    public IMongoClient MongoClient { get; private set; } = null!;
    public IMongoDatabase Database { get; private set; } = null!;

    public MongoDbFixture()
    {
        _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:7.0")
            .WithPortBinding(27017, true)
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _mongoContainer.StartAsync();
        ConnectionString = _mongoContainer.GetConnectionString();
        MongoClient = new MongoClient(ConnectionString);
        Database = MongoClient.GetDatabase("octo_integration_test");
    }

    public async Task DisposeAsync()
    {
        await _mongoContainer.DisposeAsync();
    }
}
```

---

## Projektstruktur

### Neues Integrationtest-Projekt

```
tests/
├── MeshAdapter.Sdk.Tests/              # Unit Tests (bestehend)
│   └── ...
│
└── MeshAdapter.Sdk.IntegrationTests/   # Neues Projekt
    ├── MeshAdapter.Sdk.IntegrationTests.csproj
    ├── Fixtures/
    │   ├── MongoDbFixture.cs           # MongoDB Container Lifecycle
    │   ├── TenantRepositoryFixture.cs  # Repository Setup
    │   └── CkCacheFixture.cs           # CK Cache Setup
    ├── Helpers/
    │   ├── IntegrationTestBase.cs      # Basis-Klasse für Tests
    │   ├── TestDataSeeder.cs           # Daten-Seeding
    │   └── TestTenantRepository.cs     # Test-Repository Wrapper
    ├── TestData/
    │   ├── ConstructionKits/           # CK JSON Definitionen
    │   │   └── system-2.0.0.json
    │   └── Queries/                    # Test Query Definitionen
    │       └── test-query.json
    └── Nodes/
        └── Extract/
            └── GetQueryByIdNodeIntegrationTests.cs
```

### Projektdatei

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <IsPackable>false</IsPackable>
        <Configurations>Debug;Release;DebugL</Configurations>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="coverlet.collector" Version="6.0.4" />
        <PackageReference Include="FakeItEasy" Version="9.0.0" />
        <PackageReference Include="FluentAssertions" Version="8.0.0" />
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
        <PackageReference Include="Testcontainers" Version="4.4.0" />
        <PackageReference Include="Testcontainers.MongoDb" Version="4.4.0" />
        <PackageReference Include="xunit" Version="2.9.3" />
        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
    </ItemGroup>

    <ItemGroup>
        <Using Include="Xunit" />
        <Using Include="FluentAssertions" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\MeshAdapter.Sdk\MeshAdapter.Sdk.csproj" />
        <ProjectReference Include="..\..\src\MeshNodes.Sdk\MeshNodes.Sdk.csproj" />
    </ItemGroup>

    <!-- Test Data als Embedded Resources -->
    <ItemGroup>
        <EmbeddedResource Include="TestData\**\*.json" />
    </ItemGroup>

</Project>
```

---

## Test-Fixtures

### IntegrationTestBase

```csharp
/// <summary>
/// Basis-Klasse für alle Integration Tests mit MongoDB
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<MongoDbFixture>, IAsyncLifetime
{
    protected readonly MongoDbFixture MongoFixture;
    protected ITenantRepository TenantRepository { get; private set; } = null!;
    protected ICkCacheService CkCacheService { get; private set; } = null!;
    protected IMeshEtlContext MeshEtlContext { get; private set; } = null!;
    protected IServiceProvider ServiceProvider { get; private set; } = null!;

    protected readonly string TenantId = "integration-test-tenant";
    protected readonly OctoObjectId TestPipelineId = new("test-pipeline-001");

    protected IntegrationTestBase(MongoDbFixture mongoFixture)
    {
        MongoFixture = mongoFixture;
    }

    public virtual async Task InitializeAsync()
    {
        // Services aufbauen
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        // Repository und Context erstellen
        TenantRepository = await CreateTenantRepositoryAsync();
        CkCacheService = CreateCkCacheService();
        MeshEtlContext = CreateMeshEtlContext();

        // Testdaten seeden
        await SeedTestDataAsync();
    }

    public virtual async Task DisposeAsync()
    {
        // Cleanup: Testdaten löschen
        await CleanupTestDataAsync();
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Überschreibbar für zusätzliche Services
    }

    protected abstract Task<ITenantRepository> CreateTenantRepositoryAsync();

    protected virtual ICkCacheService CreateCkCacheService()
    {
        // In-Memory CK Cache mit Test-CK laden
        return new TestCkCacheService(TenantId);
    }

    protected virtual IMeshEtlContext CreateMeshEtlContext()
    {
        return new MeshEtlContext(
            tenantId: TenantId,
            tenantRepository: TenantRepository,
            dataFlowRtId: TestPipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId(TenantId, TestPipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: new TestGlobalConfiguration(),
            properties: new Dictionary<string, object?>()
        );
    }

    protected virtual Task SeedTestDataAsync() => Task.CompletedTask;

    protected virtual Task CleanupTestDataAsync() => Task.CompletedTask;

    /// <summary>
    /// Erstellt einen DataContext für Tests
    /// </summary>
    protected IDataContext CreateDataContext(JToken? initialData = null)
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(initialData ?? new JObject());
        return dataContext;
    }

    /// <summary>
    /// Erstellt einen NodeContext für Tests
    /// </summary>
    protected INodeContext CreateNodeContext<TConfig>(TConfig config)
        where TConfig : class, INodeConfiguration
    {
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = CreateDataContext();

        var rootContext = NodeContext.CreateRootNodeContext(
            ServiceProvider,
            logger,
            dataContext);

        return rootContext.RegisterChildNode(
            typeof(TConfig).Name.Replace("Configuration", ""),
            0,
            config,
            dataContext);
    }
}
```

### TestCkCacheService

```csharp
/// <summary>
/// In-Memory CK Cache Service für Integration Tests
/// </summary>
public class TestCkCacheService : ICkCacheService
{
    private readonly Dictionary<string, CkEntityType> _types = new();
    private readonly string _tenantId;

    public TestCkCacheService(string tenantId)
    {
        _tenantId = tenantId;
        LoadDefaultTypes();
    }

    private void LoadDefaultTypes()
    {
        // System-Typen laden (RtSimpleRtQuery, etc.)
        RegisterType(new CkEntityType
        {
            CkTypeId = "System/RtSimpleRtQuery",
            Attributes = new List<CkAttribute>
            {
                new() { AttributeId = "QueryCkTypeId", ValueType = AttributeValueTypes.String },
                new() { AttributeId = "Columns", ValueType = AttributeValueTypes.StringArray },
                new() { AttributeId = "FieldFilter", ValueType = AttributeValueTypes.RecordArray },
                new() { AttributeId = "Sorting", ValueType = AttributeValueTypes.RecordArray },
                // ... weitere Attribute
            }
        });

        // Test-Typen registrieren
        RegisterType(new CkEntityType
        {
            CkTypeId = "Test/Customer",
            Attributes = new List<CkAttribute>
            {
                new() { AttributeId = "Name", ValueType = AttributeValueTypes.String },
                new() { AttributeId = "Email", ValueType = AttributeValueTypes.String },
                new() { AttributeId = "CreatedAt", ValueType = AttributeValueTypes.DateTime }
            }
        });
    }

    public void RegisterType(CkEntityType type)
    {
        _types[type.CkTypeId] = type;
    }

    public CkEntityType? GetType(string tenantId, string ckTypeId)
    {
        return _types.GetValueOrDefault(ckTypeId);
    }

    // ... weitere ICkCacheService Methoden implementieren
}
```

---

## Testdaten-Management

### TestDataSeeder

```csharp
/// <summary>
/// Hilfklasse zum Seeden von Testdaten in MongoDB
/// </summary>
public class TestDataSeeder
{
    private readonly ITenantRepository _repository;

    public TestDataSeeder(ITenantRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Erstellt eine Test-Query im Repository
    /// </summary>
    public async Task<OctoObjectId> CreateTestQueryAsync(
        string queryCkTypeId,
        string[] columns,
        List<FieldFilterRecord>? fieldFilters = null,
        List<OrderItemRecord>? sorting = null)
    {
        var session = await _repository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var queryRtId = OctoObjectId.GenerateNewId();

            var query = new RtSimpleRtQuery
            {
                RtId = queryRtId,
                CkTypeId = "System/RtSimpleRtQuery",
                QueryCkTypeId = queryCkTypeId,
                Columns = columns.ToList(),
                FieldFilter = fieldFilters,
                Sorting = sorting
            };

            await _repository.InsertRtEntityAsync(session, query);
            await session.CommitTransactionAsync();

            return queryRtId;
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// Erstellt Test-Entitäten eines bestimmten Typs
    /// </summary>
    public async Task<List<OctoObjectId>> CreateTestEntitiesAsync<TEntity>(
        int count,
        Func<int, TEntity> entityFactory)
        where TEntity : RtEntity
    {
        var session = await _repository.GetSessionAsync();
        session.StartTransaction();

        var ids = new List<OctoObjectId>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                var entity = entityFactory(i);
                entity.RtId = OctoObjectId.GenerateNewId();
                await _repository.InsertRtEntityAsync(session, entity);
                ids.Add(entity.RtId);
            }

            await session.CommitTransactionAsync();
            return ids;
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// Löscht alle Testdaten
    /// </summary>
    public async Task CleanupAsync()
    {
        var session = await _repository.GetSessionAsync();
        // Collections leeren oder Datenbank droppen
        await _repository.DropDatabaseAsync(session);
    }
}
```

### Test-Entitäten Builder

```csharp
/// <summary>
/// Builder für Test-Entitäten
/// </summary>
public static class TestEntityBuilder
{
    public static RtEntity CreateCustomer(int index)
    {
        return new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = "Test/Customer",
            Attributes = new Dictionary<string, object?>
            {
                ["Name"] = $"Customer {index}",
                ["Email"] = $"customer{index}@test.com",
                ["CreatedAt"] = DateTime.UtcNow.AddDays(-index)
            }
        };
    }

    public static RtSimpleRtQuery CreateQuery(
        string queryCkTypeId,
        params string[] columns)
    {
        return new RtSimpleRtQuery
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = "System/RtSimpleRtQuery",
            QueryCkTypeId = queryCkTypeId,
            Columns = columns.ToList()
        };
    }
}
```

---

## Beispiel: GetQueryByIdNode

### Integration Test Klasse

```csharp
/// <summary>
/// Integration Tests für GetQueryByIdNode
/// </summary>
public class GetQueryByIdNodeIntegrationTests : IntegrationTestBase
{
    private TestDataSeeder _seeder = null!;
    private OctoObjectId _testQueryId;
    private List<OctoObjectId> _testCustomerIds = new();

    public GetQueryByIdNodeIntegrationTests(MongoDbFixture mongoFixture)
        : base(mongoFixture)
    {
    }

    protected override async Task<ITenantRepository> CreateTenantRepositoryAsync()
    {
        // Reales Repository mit Testcontainer MongoDB erstellen
        var connectionString = MongoFixture.ConnectionString;

        // Repository-Factory oder direktes Erstellen
        return await TenantRepositoryFactory.CreateAsync(
            connectionString,
            TenantId);
    }

    protected override async Task SeedTestDataAsync()
    {
        _seeder = new TestDataSeeder(TenantRepository);

        // 1. Test-Kunden erstellen
        _testCustomerIds = await _seeder.CreateTestEntitiesAsync(
            count: 10,
            entityFactory: TestEntityBuilder.CreateCustomer);

        // 2. Test-Query erstellen
        _testQueryId = await _seeder.CreateTestQueryAsync(
            queryCkTypeId: "Test/Customer",
            columns: new[] { "Name", "Email", "CreatedAt" },
            sorting: new List<OrderItemRecord>
            {
                new() { AttributePath = "Name", SortOrder = SortOrders.Ascending }
            });
    }

    protected override async Task CleanupTestDataAsync()
    {
        await _seeder.CleanupAsync();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithValidQueryId_ReturnsQueryResult()
    {
        // Arrange
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = _testQueryId,
            TargetPath = "$.queryResult"
        };

        var dataContext = CreateDataContext();
        var nodeContext = CreateNodeContext(config);
        var next = A.Fake<NodeDelegate>();

        QueryResult? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath<QueryResult>(
            "$.queryResult",
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _,
                      TargetValueWriteModes _, QueryResult result) =>
            {
                capturedResult = result;
            });

        var node = new GetQueryByIdNode(next, MeshEtlContext, CkCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert
        capturedResult.Should().NotBeNull();
        capturedResult!.Rows.Should().HaveCount(10);
        capturedResult.Columns.Should().HaveCount(3);
        capturedResult.Columns.Select(c => c.Header)
            .Should().BeEquivalentTo(new[] { "Name", "Email", "CreatedAt" });

        // Verify sorted by Name ascending
        capturedResult.Rows.Select(r => r.Values[0]?.ToString())
            .Should().BeInAscendingOrder();

        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithPagination_ReturnsPagedResult()
    {
        // Arrange
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = _testQueryId,
            TargetPath = "$.queryResult",
            Skip = 2,
            Take = 5
        };

        var dataContext = CreateDataContext();
        var nodeContext = CreateNodeContext(config);
        var next = A.Fake<NodeDelegate>();

        QueryResult? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath<QueryResult>(
            "$.queryResult",
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _,
                      TargetValueWriteModes _, QueryResult result) =>
            {
                capturedResult = result;
            });

        var node = new GetQueryByIdNode(next, MeshEtlContext, CkCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert
        capturedResult.Should().NotBeNull();
        capturedResult!.Rows.Should().HaveCount(5); // Take = 5

        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNonExistentQuery_LogsErrorAndReturns()
    {
        // Arrange
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = new OctoObjectId("non-existent-query"),
            TargetPath = "$.queryResult"
        };

        var dataContext = CreateDataContext();
        var nodeContext = CreateNodeContext(config);
        var next = A.Fake<NodeDelegate>();

        var node = new GetQueryByIdNode(next, MeshEtlContext, CkCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert: next should NOT be called when query not found
        A.CallTo(() => next(dataContext, nodeContext))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithAdditionalFieldFilters_AppliesFilters()
    {
        // Arrange
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = _testQueryId,
            TargetPath = "$.queryResult",
            FieldFilters = new List<FieldFilterWithPathDto>
            {
                new()
                {
                    AttributePath = "Name",
                    Operator = FieldFilterOperator.Contains,
                    ComparisonValue = "Customer 1"
                }
            }
        };

        var dataContext = CreateDataContext();
        var nodeContext = CreateNodeContext(config);
        var next = A.Fake<NodeDelegate>();

        QueryResult? capturedResult = null;
        A.CallTo(() => dataContext.SetValueByPath<QueryResult>(
            "$.queryResult",
            A<DocumentModes>._,
            A<ValueKinds>._,
            A<TargetValueWriteModes>._,
            A<QueryResult>._))
            .Invokes((string _, DocumentModes _, ValueKinds _,
                      TargetValueWriteModes _, QueryResult result) =>
            {
                capturedResult = result;
            });

        var node = new GetQueryByIdNode(next, MeshEtlContext, CkCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert: Only customers matching "Customer 1" filter
        capturedResult.Should().NotBeNull();
        capturedResult!.Rows.Should().OnlyContain(
            r => r.Values[0]!.ToString()!.Contains("Customer 1"));
    }
}
```

---

## CI/CD Integration

### GitHub Actions Workflow

```yaml
# .github/workflows/integration-tests.yml
name: Integration Tests

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

jobs:
  integration-tests:
    runs-on: ubuntu-latest

    services:
      # Optional: Falls kein Testcontainers verwendet wird
      # mongodb:
      #   image: mongo:7.0
      #   ports:
      #     - 27017:27017

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore

      - name: Run Unit Tests
        run: dotnet test tests/MeshAdapter.Sdk.Tests --no-build --verbosity normal

      - name: Run Integration Tests
        run: dotnet test tests/MeshAdapter.Sdk.IntegrationTests --no-build --verbosity normal
        env:
          # Testcontainers benötigt Docker
          TESTCONTAINERS_RYUK_DISABLED: true
```

### Test Kategorisierung

```csharp
// Trait für Kategorisierung
[Trait("Category", "Integration")]
public class GetQueryByIdNodeIntegrationTests : IntegrationTestBase
{
    // ...
}

// Ausführung nach Kategorie
// dotnet test --filter "Category=Integration"
// dotnet test --filter "Category!=Integration"  // Nur Unit Tests
```

---

## Zusammenfassung

### Vorteile dieses Konzepts

1. **Isolation**: Jeder Test läuft mit eigener MongoDB-Instanz (Testcontainers)
2. **Reproduzierbarkeit**: Keine Abhängigkeit von externen Systemen
3. **Parallele Ausführung**: Tests können parallel laufen
4. **Realistische Tests**: Echte Datenbank-Interaktionen werden getestet
5. **CI/CD-tauglich**: Läuft in GitHub Actions/Azure DevOps

### Nächste Schritte

1. [ ] Projekt `MeshAdapter.Sdk.IntegrationTests` erstellen
2. [ ] `MongoDbFixture` implementieren
3. [ ] `IntegrationTestBase` implementieren
4. [ ] `TestCkCacheService` implementieren
5. [ ] Ersten Integration Test für `GetQueryByIdNode` schreiben
6. [ ] CI/CD Pipeline erweitern

### Abhängigkeiten

| Paket | Version | Zweck |
|-------|---------|-------|
| Testcontainers | 4.4.0 | Docker Container Management |
| Testcontainers.MongoDb | 4.4.0 | MongoDB Container |
| FluentAssertions | 8.0.0 | Lesbare Assertions |
| xUnit | 2.9.3 | Test Framework |
| FakeItEasy | 9.0.0 | Mocking (für DataContext etc.) |
