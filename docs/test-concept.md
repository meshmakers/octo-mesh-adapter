# Octo Mesh Adapter - Test Concept

This document describes the testing strategy for the Octo Mesh Adapter, covering unit tests and integration tests for pipeline nodes and services.

## Table of Contents

1. [Overview](#overview)
2. [Test Architecture](#test-architecture)
3. [Test Frameworks & Tools](#test-frameworks--tools)
4. [Unit Tests](#unit-tests)
   - [Extract Nodes](#extract-nodes)
   - [Transform Nodes](#transform-nodes)
   - [Load Nodes](#load-nodes)
   - [Trigger Nodes](#trigger-nodes)
   - [Services](#services)
5. [Integration Tests](#integration-tests)
6. [Mocking Strategy](#mocking-strategy)
7. [Test Data Management](#test-data-management)
8. [Code Examples](#code-examples)
9. [Coverage Goals](#coverage-goals)

---

## Overview

### Testing Goals

1. **Reliability**: Ensure all pipeline nodes process data correctly
2. **Regression Prevention**: Catch breaking changes early
3. **Documentation**: Tests serve as executable documentation
4. **Confidence**: Enable safe refactoring and feature additions

### Test Pyramid

```
        /\
       /  \     E2E Tests (Manual/Smoke)
      /----\
     /      \   Integration Tests
    /--------\
   /          \  Unit Tests (Foundation)
  /------------\
```

- **Unit Tests** (70%): Individual node logic, isolated with mocks
- **Integration Tests** (25%): Multi-node pipelines, service interactions
- **E2E Tests** (5%): Full system with real MongoDB/CrateDB

---

## Test Architecture

### Project Structure

```
tests/
├── MeshAdapter.Sdk.Tests/
│   ├── Nodes/
│   │   ├── Extract/
│   │   │   ├── GetRtEntitiesByIdNodeTests.cs
│   │   │   ├── GetRtEntitiesByTypeNodeTests.cs
│   │   │   ├── GetAssociationTargetsNodeTests.cs
│   │   │   ├── GetQueryByIdNodeTests.cs
│   │   │   └── EnrichWithMongoDataNodeTests.cs
│   │   ├── Transform/
│   │   │   ├── DistinctNodeTests.cs              (existing)
│   │   │   ├── StatisticalAnomalyNodeTests.cs    (existing)
│   │   │   ├── MachineLearningAnomalyNodeTests.cs (existing)
│   │   │   ├── DataMappingNodeTests.cs
│   │   │   ├── CreateUpdateInfoNodeTests.cs
│   │   │   ├── MakeHttpRequestNodeTests.cs
│   │   │   ├── ImportFromExcelNodeTests.cs
│   │   │   ├── PdfOcrExtractionNodeTests.cs
│   │   │   └── PlaceholderReplaceNodeTests.cs
│   │   ├── Load/
│   │   │   ├── ApplyChangesNodeTests.cs
│   │   │   ├── SaveInTimeSeriesNodeTests.cs
│   │   │   └── EMailSenderNodeTests.cs
│   │   └── Trigger/
│   │       ├── FromHttpRequestNodeTests.cs
│   │       ├── FromWatchRtEntityNodeTests.cs
│   │       ├── FromExecutePipelineCommandNodeTests.cs
│   │       └── FromEmailNodeTests.cs
│   ├── Services/
│   │   ├── MeshEtlContextTests.cs
│   │   ├── MeshContextCreatorServiceTests.cs
│   │   ├── HttpRequestServiceTests.cs
│   │   └── MeshAdapterServiceTests.cs
│   ├── Helpers/
│   │   ├── TestDataBuilder.cs
│   │   ├── NodeTestBase.cs
│   │   └── MockFactory.cs
│   └── Integration/
│       ├── PipelineExecutionTests.cs
│       ├── ExtractTransformLoadTests.cs
│       └── TriggerPipelineTests.cs
├── MeshAdapter.Sdk.IntegrationTests/
│   ├── MongoDbIntegrationTests.cs
│   ├── CrateDbIntegrationTests.cs
│   └── FullPipelineTests.cs
└── MeshAdapter.Sdk.Tests.csproj
```

---

## Test Frameworks & Tools

### Current Stack

| Tool | Version | Purpose |
|------|---------|---------|
| xUnit | 2.9.3 | Test framework |
| FakeItEasy | 9.0.0 | Mocking library |
| coverlet.collector | 6.0.4 | Code coverage |
| Microsoft.NET.Test.Sdk | 18.0.1 | Test runner |

### Additional Recommendations

| Tool | Purpose |
|------|---------|
| FluentAssertions | More readable assertions |
| Testcontainers | Docker-based integration tests |
| Bogus | Fake data generation |
| Verify | Snapshot testing for complex outputs |

---

## Unit Tests

### General Pattern

All unit tests follow the **Arrange-Act-Assert** pattern:

```csharp
[Fact]
public async Task ProcessObjectAsync_WithValidInput_ProducesExpectedOutput()
{
    // Arrange
    var (dataContext, nodeContext, next) = PrepareTest(inputData, configuration);
    var node = new TargetNode(next, dependencies);

    // Act
    await node.ProcessObjectAsync(dataContext, nodeContext);

    // Assert
    A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    A.CallTo(() => dataContext.SetValueByPath(...)).MustHaveHappened();
}
```

---

### Extract Nodes

Extract nodes retrieve data from repositories. Tests must mock `ITenantRepository` and verify correct query parameters.

#### GetRtEntitiesByIdNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithValidIds_ReturnsEntities` | Returns entities for given IDs |
| `ProcessObjectAsync_WithEmptyIds_ReturnsEmptyResult` | Handles empty ID list |
| `ProcessObjectAsync_WithNonExistentIds_ReturnsPartialResult` | Handles missing entities |
| `ProcessObjectAsync_WithFieldFilters_AppliesFilters` | Field filtering works |
| `ProcessObjectAsync_WithPagination_RespectsSkipTake` | Pagination parameters |
| `ProcessObjectAsync_WithPathBasedCkTypeId_ResolvesPath` | Dynamic type resolution |

**Mocking Requirements**:
```csharp
var tenantRepository = A.Fake<ITenantRepository>();
var session = A.Fake<ISession>();

A.CallTo(() => meshEtlContext.TenantRepository).Returns(tenantRepository);
A.CallTo(() => tenantRepository.GetSessionAsync()).Returns(session);
A.CallTo(() => tenantRepository.GetRtEntitiesByIdAsync(session, A<OdataQueryOptions>._))
    .Returns(expectedEntities);
```

#### GetRtEntitiesByTypeNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithValidType_ReturnsAllEntities` | Returns all entities of type |
| `ProcessObjectAsync_WithFieldFilters_FiltersCorrectly` | Field-based filtering |
| `ProcessObjectAsync_WithSortOrders_SortsCorrectly` | Sort order application |
| `ProcessObjectAsync_WithPagination_PaginatesResults` | Skip/Take functionality |

#### GetAssociationTargetsNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithOutboundDirection_ReturnsTargets` | Outbound associations |
| `ProcessObjectAsync_WithInboundDirection_ReturnsOrigins` | Inbound associations |
| `ProcessObjectAsync_WithAnyDirection_ReturnsBoth` | Bidirectional query |
| `ProcessObjectAsync_WithNoAssociations_ReturnsEmpty` | No associations found |

---

### Transform Nodes

Transform nodes process data without external dependencies. Focus on data transformation logic.

#### DataMappingNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithIntMapping_MapsCorrectly` | Integer value mapping |
| `ProcessObjectAsync_WithStringMapping_MapsCorrectly` | String value mapping |
| `ProcessObjectAsync_WithUnmappedValue_ReturnsOriginal` | Unmapped value handling |
| `ProcessObjectAsync_WithTypeConversion_ConvertsTypes` | Type conversion (Int→String) |
| `ProcessObjectAsync_WithNullValue_HandlesNull` | Null input handling |

#### CreateUpdateInfoNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithInsertKind_CreatesInsertInfo` | Insert operation |
| `ProcessObjectAsync_WithUpdateKind_CreatesUpdateInfo` | Update operation |
| `ProcessObjectAsync_WithDeleteKind_CreatesDeleteInfo` | Delete operation |
| `ProcessObjectAsync_WithAttributeUpdates_IncludesAttributes` | Attribute updates |
| `ProcessObjectAsync_WithPathBasedRtId_ResolvesPath` | Dynamic ID resolution |
| `ProcessObjectAsync_WithTimestampPath_UsesExternalTimestamp` | External timestamp |

#### MakeHttpRequestNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithGetRequest_ExecutesGet` | GET request execution |
| `ProcessObjectAsync_WithPostRequest_SendsBody` | POST with body |
| `ProcessObjectAsync_WithHeaderParameters_AddsHeaders` | Dynamic headers |
| `ProcessObjectAsync_WithPathParameters_SubstitutesPath` | URL path substitution |
| `ProcessObjectAsync_WithErrorResponse_HandlesError` | HTTP error handling |
| `ProcessObjectAsync_WithJsonResponse_ParsesJson` | JSON response parsing |

**Mocking Requirements**:
```csharp
var httpClientFactory = A.Fake<IHttpClientFactory>();
var httpClient = new HttpClient(new MockHttpMessageHandler(expectedResponse));
A.CallTo(() => httpClientFactory.CreateClient(A<string>._)).Returns(httpClient);
```

#### PdfOcrExtractionNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithValidPdf_ExtractsText` | Basic text extraction |
| `ProcessObjectAsync_WithSpecificPages_ProcessesPages` | Page selection |
| `ProcessObjectAsync_WithGermanLanguage_UsesGermanOcr` | Language configuration |
| `ProcessObjectAsync_WithTableExtraction_ExtractsTables` | Table extraction |
| `ProcessObjectAsync_WithBarcodeExtraction_FindsBarcodes` | Barcode detection |
| `ProcessObjectAsync_WithInvalidPdf_HandlesError` | Error handling |
| `ProcessObjectAsync_WithContinueOnError_ContinuesPipeline` | Error recovery |

#### ImportFromExcelNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithTreePathImport_CreatesHierarchy` | Hierarchical path import |
| `ProcessObjectAsync_WithTreeColumnImport_CreatesParentChild` | Parent-child import |
| `ProcessObjectAsync_WithColumnMapping_MapsColumns` | Column mapping |
| `ProcessObjectAsync_WithEmptyExcel_ReturnsEmpty` | Empty file handling |

#### DistinctNode (existing - extend)

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithDuplicates_RemovesDuplicates` | Basic deduplication |
| `ProcessObjectAsync_WithNestedPath_DeduplicatesNested` | Nested property path |
| `ProcessObjectAsync_WithEmptyArray_ReturnsEmpty` | Empty input |
| `ProcessObjectAsync_WithNoDuplicates_ReturnsOriginal` | No duplicates case |

---

### Load Nodes

Load nodes persist data. Tests must verify correct repository method calls and transaction handling.

#### ApplyChangesNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithInserts_InsertsEntities` | Insert operations |
| `ProcessObjectAsync_WithUpdates_UpdatesEntities` | Update operations |
| `ProcessObjectAsync_WithDeletes_DeletesEntities` | Delete operations |
| `ProcessObjectAsync_WithMixedOperations_ProcessesAll` | Mixed operations |
| `ProcessObjectAsync_WithWriteConflict_RetriesOperation` | Retry on conflict |
| `ProcessObjectAsync_WithTransactionFailure_RollsBack` | Transaction rollback |
| `ProcessObjectAsync_WithDuplicateUpdates_KeepsLatest` | Duplicate handling |
| `ProcessObjectAsync_ConcurrentCalls_UseSemaphore` | Concurrency control |

**Mocking Requirements**:
```csharp
var session = A.Fake<ISession>();
A.CallTo(() => tenantRepository.GetSessionAsync()).Returns(session);
A.CallTo(() => session.StartTransaction()).DoesNothing();
A.CallTo(() => session.CommitTransactionAsync()).Returns(Task.CompletedTask);
A.CallTo(() => session.AbortTransactionAsync()).Returns(Task.CompletedTask);

// Verify transaction lifecycle
A.CallTo(() => session.StartTransaction()).MustHaveHappenedOnceExactly();
A.CallTo(() => session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
```

#### SaveInTimeSeriesNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithValidData_SavesTimeSeries` | Basic save operation |
| `ProcessObjectAsync_WithTimestamp_UsesCorrectTimestamp` | Timestamp handling |
| `ProcessObjectAsync_WithAttributes_SavesAllAttributes` | Attribute persistence |
| `ProcessObjectAsync_WithEmptyData_HandlesGracefully` | Empty input handling |

#### EMailSenderNode

| Test Case | Description |
|-----------|-------------|
| `ProcessObjectAsync_WithValidEmail_SendsEmail` | Basic email sending |
| `ProcessObjectAsync_WithMultipleRecipients_SendsToAll` | Multiple recipients |
| `ProcessObjectAsync_WithMarkdownBody_ConvertsToHtml` | Markdown conversion |
| `ProcessObjectAsync_WithSemaphore_LimitsConcurrency` | Concurrency control |
| `ProcessObjectAsync_WithSmtpError_HandlesError` | Error handling |

---

### Trigger Nodes

Trigger nodes implement `ITriggerPipelineNode`. Test both `StartAsync` and `StopAsync` lifecycle methods.

#### FromHttpRequestNode

| Test Case | Description |
|-----------|-------------|
| `StartAsync_WithValidConfig_RegistersRoute` | Route registration |
| `StartAsync_WithGetMethod_RegistersGetEndpoint` | GET endpoint |
| `StartAsync_WithPostMethod_RegistersPostEndpoint` | POST endpoint |
| `StopAsync_AfterStart_UnregistersRoute` | Route cleanup |
| `RequestHandler_WithJsonBody_ParsesJson` | JSON body parsing |
| `RequestHandler_WithMultipartForm_ParsesFiles` | File upload handling |

**Mocking Requirements**:
```csharp
var httpRequestService = A.Fake<IHttpRequestService>();
var triggerContext = A.Fake<ITriggerContext>();
var routeHandle = A.Fake<HttpRouteHandle>();

A.CallTo(() => httpRequestService.CreateRoute(A<HttpRequestOptions>._))
    .Returns(routeHandle);
```

#### FromWatchRtEntityNode

| Test Case | Description |
|-----------|-------------|
| `StartAsync_WithCkTypeId_StartsWatching` | Entity watching |
| `StartAsync_WithFieldFilters_AppliesFilters` | Filter configuration |
| `StopAsync_AfterStart_StopsWatching` | Cleanup |
| `EntityChanged_WithInsert_TriggersPipeline` | Insert trigger |
| `EntityChanged_WithUpdate_TriggersPipeline` | Update trigger |
| `EntityChanged_WithDelete_TriggersPipeline` | Delete trigger |
| `EntityChanged_WithBeforeFilters_ChecksPreviousValues` | Before-state filtering |

#### FromExecutePipelineCommandNode

| Test Case | Description |
|-----------|-------------|
| `StartAsync_WithValidConfig_RegistersConsumer` | Command consumer registration |
| `StopAsync_AfterStart_UnregistersConsumer` | Cleanup |
| `CommandReceived_WithPayload_TriggersPipeline` | Command handling |
| `CommandReceived_WithCallback_ReturnsResult` | Response callback |

#### FromEmailNode

| Test Case | Description |
|-----------|-------------|
| `StartAsync_WithImapConfig_ConnectsToServer` | IMAP connection |
| `StartAsync_WithPollingInterval_StartsPolling` | Polling setup |
| `StopAsync_AfterStart_DisconnectsAndStops` | Cleanup |
| `EmailReceived_WithSenderFilter_FiltersCorrectly` | Sender filtering |
| `EmailReceived_WithSubjectFilter_FiltersCorrectly` | Subject filtering |
| `EmailReceived_WithAttachments_IncludesAttachments` | Attachment handling |

---

### Services

#### MeshEtlContext

| Test Case | Description |
|-----------|-------------|
| `Constructor_WithValidParams_InitializesCorrectly` | Initialization |
| `Properties_AfterSet_ReturnsValue` | Property storage |
| `TenantRepository_ReturnsInjectedRepository` | Repository access |

#### MeshContextCreatorService

| Test Case | Description |
|-----------|-------------|
| `CreateEtlContext_WithValidRegistration_CreatesContext` | Context creation |
| `CreateEtlContext_LoadsCkCache` | CK cache loading |
| `CreateEtlContext_WithInvalidTenant_ThrowsException` | Error handling |

#### HttpRequestService

| Test Case | Description |
|-----------|-------------|
| `CreateRoute_WithValidOptions_RegistersRoute` | Route registration |
| `CreateRoute_WithDuplicatePath_ThrowsException` | Duplicate handling |
| `RemoveRoute_WithExistingRoute_Removes` | Route removal |
| `SendRequestAsync_WithRegisteredRoute_ProcessesRequest` | Request processing |

---

## Integration Tests

Integration tests verify multi-node pipeline execution and service interactions.

### Pipeline Execution Tests

#### ExtractTransformLoadTests

```csharp
[Fact]
public async Task Pipeline_ExtractTransformLoad_ProcessesDataCorrectly()
{
    // Arrange: Setup pipeline with Extract → Transform → Load nodes
    var pipeline = CreatePipeline(
        new GetRtEntitiesByTypeNode(...),
        new CreateUpdateInfoNode(...),
        new ApplyChangesNode(...)
    );

    // Act: Execute pipeline
    var result = await pipeline.ExecuteAsync(inputData);

    // Assert: Verify final state
    Assert.NotNull(result);
    // Verify database state if using test database
}
```

#### TriggerPipelineTests

```csharp
[Fact]
public async Task HttpTrigger_WithPostRequest_ExecutesPipeline()
{
    // Arrange: Setup HTTP trigger with pipeline
    var trigger = new FromHttpRequestNode(...);
    await trigger.StartAsync(triggerContext);

    // Act: Simulate HTTP request
    var response = await httpClient.PostAsync("/test-endpoint", content);

    // Assert: Verify pipeline executed
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    // Verify expected side effects
}
```

### Database Integration Tests

For integration tests with real databases, use Testcontainers:

```csharp
public class MongoDbIntegrationTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongoContainer;

    public MongoDbIntegrationTests()
    {
        _mongoContainer = new MongoDbBuilder()
            .WithImage("mongo:7.0")
            .Build();
    }

    public async Task InitializeAsync() => await _mongoContainer.StartAsync();
    public async Task DisposeAsync() => await _mongoContainer.DisposeAsync();

    [Fact]
    public async Task ApplyChangesNode_WithRealMongo_PersistsData()
    {
        // Arrange
        var connectionString = _mongoContainer.GetConnectionString();
        var repository = CreateRepository(connectionString);

        // Act & Assert
        // Test with real MongoDB
    }
}
```

---

## Mocking Strategy

### Core Interfaces to Mock

| Interface | Mock Approach | Key Methods |
|-----------|---------------|-------------|
| `IDataContext` | FakeItEasy | `Current`, `GetComplexObjectByPath`, `SetValueByPath` |
| `INodeContext` | FakeItEasy | `GetNodeConfiguration`, logging methods |
| `IMeshEtlContext` | FakeItEasy | `Properties`, `TenantRepository` |
| `ITenantRepository` | FakeItEasy | `GetSessionAsync`, CRUD operations |
| `ISession` | FakeItEasy | Transaction methods |
| `NodeDelegate` | FakeItEasy | Verify chaining |

### Mock Factory Helper

```csharp
public static class MockFactory
{
    public static IDataContext CreateDataContext(JToken? current = null)
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Current).Returns(current ?? new JObject());
        return dataContext;
    }

    public static IMeshEtlContext CreateMeshEtlContext(
        ITenantRepository? repository = null)
    {
        var context = A.Fake<IMeshEtlContext>();
        A.CallTo(() => context.Properties)
            .Returns(new Dictionary<string, object?>());
        A.CallTo(() => context.TenantRepository)
            .Returns(repository ?? A.Fake<ITenantRepository>());
        return context;
    }

    public static INodeContext CreateNodeContext<TConfig>(
        TConfig configuration,
        IServiceProvider? serviceProvider = null)
        where TConfig : class
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        var rootContext = NodeContext.CreateRootNodeContext(
            serviceProvider ?? services.BuildServiceProvider(),
            logger,
            dataContext);

        return rootContext.RegisterChildNode(
            typeof(TConfig).Name, 0, configuration, dataContext);
    }
}
```

### Capturing Method Invocations

```csharp
// Capture SetValueByPath calls
List<object>? capturedValue = null;
A.CallTo(() => dataContext.SetValueByPath(
    targetPath,
    A<List<object>>._,
    A<DocumentMode>._,
    A<ValueKind>._,
    A<ValueWriteMode>._,
    A<JsonSerializer>._))
    .Invokes((string _, List<object> value, DocumentMode _,
              ValueKind _, ValueWriteMode _, JsonSerializer _) =>
    {
        capturedValue = value;
    });
```

---

## Test Data Management

### Test Data Builder

```csharp
public class TestDataBuilder
{
    public static JObject CreateEntity(
        string rtId = "test-id",
        string ckTypeId = "TestType",
        Dictionary<string, object>? attributes = null)
    {
        var entity = new JObject
        {
            ["RtId"] = rtId,
            ["CkTypeId"] = ckTypeId
        };

        if (attributes != null)
        {
            foreach (var (key, value) in attributes)
            {
                entity[key] = JToken.FromObject(value);
            }
        }

        return entity;
    }

    public static JArray CreateEntityArray(int count, string typePrefix = "Entity")
    {
        var array = new JArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(CreateEntity($"{typePrefix}-{i}"));
        }
        return array;
    }

    public static EntityUpdateInfo<RtEntity> CreateUpdateInfo(
        UpdateKind kind = UpdateKind.Update,
        string? rtId = null)
    {
        return new EntityUpdateInfo<RtEntity>
        {
            UpdateKind = kind,
            RtId = rtId ?? Guid.NewGuid().ToString(),
            // ... additional properties
        };
    }
}
```

### Embedded Test Resources

```csharp
public static class TestResources
{
    public static byte[] GetTestPdf()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(
            "MeshAdapter.Sdk.Tests.Resources.test-document.pdf");
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] GetTestExcel()
    {
        // Similar implementation for Excel files
    }
}
```

---

## Code Examples

### Complete Unit Test Example

```csharp
public class CreateUpdateInfoNodeTests
{
    private readonly JsonSerializer _serializer = JsonSerializer.CreateDefault();

    [Fact]
    public async Task ProcessObjectAsync_WithInsertKind_CreatesInsertUpdateInfo()
    {
        // Arrange
        var configuration = new CreateUpdateInfoNodeConfiguration
        {
            RtId = "entity-123",
            CkTypeId = "Customer",
            UpdateKind = UpdateKind.Insert,
            TargetPath = "$.UpdateInfo",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new() { AttributeId = "Name", Value = "Test Customer" }
            }
        };

        var dataContext = MockFactory.CreateDataContext();
        var nodeContext = MockFactory.CreateNodeContext(configuration);
        var meshEtlContext = MockFactory.CreateMeshEtlContext();
        var next = A.Fake<NodeDelegate>();

        EntityUpdateInfo<RtEntity>? capturedUpdateInfo = null;
        A.CallTo(() => dataContext.SetValueByPath(
            "$.UpdateInfo",
            A<EntityUpdateInfo<RtEntity>>._,
            A<DocumentMode>._,
            A<ValueKind>._,
            A<ValueWriteMode>._,
            A<JsonSerializer>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity> info,
                      DocumentMode _, ValueKind _, ValueWriteMode _, JsonSerializer _) =>
            {
                capturedUpdateInfo = info;
            });

        var node = new CreateUpdateInfoNode(next, meshEtlContext);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert
        Assert.NotNull(capturedUpdateInfo);
        Assert.Equal(UpdateKind.Insert, capturedUpdateInfo.UpdateKind);
        Assert.Equal("entity-123", capturedUpdateInfo.RtId);
        Assert.Equal("Customer", capturedUpdateInfo.CkTypeId);

        A.CallTo(() => next(dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(UpdateKind.Insert)]
    [InlineData(UpdateKind.Update)]
    [InlineData(UpdateKind.Delete)]
    public async Task ProcessObjectAsync_WithDifferentUpdateKinds_SetsCorrectKind(
        UpdateKind expectedKind)
    {
        // Arrange
        var configuration = new CreateUpdateInfoNodeConfiguration
        {
            RtId = "test-id",
            CkTypeId = "TestType",
            UpdateKind = expectedKind,
            TargetPath = "$.Result"
        };

        // ... setup mocks ...

        // Act & Assert
        // ... verify UpdateKind is set correctly ...
    }
}
```

### Integration Test Example

```csharp
public class ExtractTransformLoadPipelineTests
{
    [Fact]
    public async Task Pipeline_WithMultipleNodes_ProcessesSequentially()
    {
        // Arrange
        var services = new ServiceCollection();
        var executionOrder = new List<string>();

        var extractNode = CreateTrackedNode("Extract", executionOrder);
        var transformNode = CreateTrackedNode("Transform", executionOrder);
        var loadNode = CreateTrackedNode("Load", executionOrder);

        var pipeline = new PipelineBuilder()
            .Add(extractNode)
            .Add(transformNode)
            .Add(loadNode)
            .Build();

        var inputData = TestDataBuilder.CreateEntity();

        // Act
        await pipeline.ExecuteAsync(inputData);

        // Assert
        Assert.Equal(new[] { "Extract", "Transform", "Load" }, executionOrder);
    }

    private IPipelineNode CreateTrackedNode(string name, List<string> tracker)
    {
        var node = A.Fake<IPipelineNode>();
        A.CallTo(() => node.ProcessObjectAsync(A<IDataContext>._, A<INodeContext>._))
            .Invokes(() => tracker.Add(name));
        return node;
    }
}
```

---

## Coverage Goals

### Target Coverage by Category

| Category | Target | Priority |
|----------|--------|----------|
| Transform Nodes | 90% | High |
| Extract Nodes | 85% | High |
| Load Nodes | 85% | High |
| Trigger Nodes | 80% | Medium |
| Core Services | 85% | High |
| Error Handling | 90% | High |
| Edge Cases | 80% | Medium |

### Critical Paths (100% Coverage Required)

1. **Transaction handling** in ApplyChangesNode
2. **Error recovery** with retry logic
3. **Data type conversions** in DataMappingNode
4. **Path resolution** for dynamic configurations
5. **Null/empty input handling** in all nodes

### Running Tests with Coverage

```bash
# Run all tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Generate coverage report
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html

# Run specific test category
dotnet test --filter "Category=UnitTest"
dotnet test --filter "Category=IntegrationTest"
```

---

## Test Execution Commands

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"

# Run specific test class
dotnet test --filter "FullyQualifiedName~DistinctNodeTests"

# Run tests in parallel (default)
dotnet test --parallel

# Run tests sequentially (for integration tests)
dotnet test -- xunit.parallelizeTestCollections=false
```

---

## Appendix: Test Checklist for New Nodes

When implementing tests for a new node, ensure coverage of:

- [ ] Happy path with valid input
- [ ] Empty/null input handling
- [ ] Path-based configuration resolution (e.g., `RtIdPath` vs `RtId`)
- [ ] All configuration options
- [ ] Error conditions and exceptions
- [ ] `next` delegate is called (pipeline continues)
- [ ] Output written to correct target path
- [ ] Correct value types and serialization
- [ ] Transaction handling (for Load nodes)
- [ ] Cleanup/disposal (for Trigger nodes)
