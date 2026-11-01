using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     Credential guard regression tests for pipelines: <see cref="PipelineExecutionFixture" /> (real orchestrator, real
///     nodes, MongoDB) with the real <c>System.Identity</c> model (<see cref="SystemIdentitySnapshotCkModel" />), a log capture
///     and one user whose credential attributes hold known dummy values, assigned to one role. The identity service writes
///     those attributes through the repository; the fixture does the same, every pipeline path is the one under test.
/// </summary>
public class IdentityCredentialPipelineFixture : PipelineExecutionFixture
{
    /// <summary>Fixed test constants - never real credentials. No pipeline output, error or log may contain them.</summary>
    public const string DummyPasswordHash = "DUMMY-HASH-7781";

    public const string DummySecurityStamp = "DUMMY-STAMP-7781";

    public const string DummyTokenValue = "DUMMY-TOKEN-7781";

    public static readonly string[] DummyValues = [DummyPasswordHash, DummySecurityStamp, DummyTokenValue];

    public const string UserCkTypeId = "System.Identity/User";

    public const string RoleCkTypeId = "System.Identity/Role";

    public const string AssignedRoleId = "System.Identity/AssignedRole";

    public const string ProbeUserName = "probe-user-7781";

    public const string ProbeUserEmail = "probe-user-7781@example.invalid";

    /// <summary>Every log line of the host (text with exception).</summary>
    public List<string> Logs { get; } = [];

    public string ProbeUserRtId { get; private set; } = string.Empty;

    public string ProbeRoleRtId { get; private set; } = string.Empty;

    protected override async Task InitializeServicesAsync()
    {
        Services.AddSystemIdentitySnapshotCkModel();
        Services.AddLogging(b => b.AddProvider(new ListLoggerProvider(Logs)));

        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var result = new OperationResult();
        await systemContext.ImportCkModelAsync(new CkModelId(SystemIdentitySnapshotCkModel.ModelFullName), result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            throw new InvalidOperationException("Failed to import System.Identity: " +
                                                string.Join(", ", result.Messages.Select(m => m.MessageText)));
        }

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(GetService<ICkCacheService>());

        var userType = new RtCkId<CkTypeId>(UserCkTypeId);
        var user = await repository.CreateTransientRtEntityByRtCkIdAsync(userType);
        user.SetAttributeValue("UserName", AttributeValueTypesDto.String, ProbeUserName);
        user.SetAttributeValue("NormalizedUserName", AttributeValueTypesDto.String, ProbeUserName.ToUpperInvariant());
        user.SetAttributeValue("Email", AttributeValueTypesDto.String, ProbeUserEmail);
        user.SetAttributeValue("NormalizedEmail", AttributeValueTypesDto.String, ProbeUserEmail.ToUpperInvariant());
        user.SetAttributeValue("EmailConfirmed", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("PhoneNumberConfirmed", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("TwoFactorEnabled", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("LockoutEnabled", AttributeValueTypesDto.Boolean, true);
        user.SetAttributeValue("AccessFailedCount", AttributeValueTypesDto.Integer, 3);
        user.SetAttributeValue("ResetPasswordOnLogin", AttributeValueTypesDto.Boolean, false);
        user.SetAttributeValue("PasswordHash", AttributeValueTypesDto.String, DummyPasswordHash);
        user.SetAttributeValue("SecurityStamp", AttributeValueTypesDto.String, DummySecurityStamp);
        user.SetAttributeValue("UserTokens", AttributeValueTypesDto.RecordArray, new List<RtRecord>
        {
            new(new RtCkId<CkRecordId>("System.Identity/UserToken"), new Dictionary<string, object?>
            {
                ["LoginProvider"] = "[AspNetUserStore]",
                ["Name"] = "AuthenticatorKey",
                ["UserId"] = ProbeUserName,
                ["Value"] = DummyTokenValue
            })
        });

        var roleType = new RtCkId<CkTypeId>(RoleCkTypeId);
        var role = await repository.CreateTransientRtEntityByRtCkIdAsync(roleType);
        role.SetAttributeValue("Name", AttributeValueTypesDto.String, "ProbeRole");
        role.SetAttributeValue("NormalizedName", AttributeValueTypesDto.String, "PROBEROLE");

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var apply = new OperationResult();
        await repository.ApplyChangesAsync(session,
            [EntityUpdateInfo<RtEntity>.CreateInsert(userType, user), EntityUpdateInfo<RtEntity>.CreateInsert(roleType, role)],
            [AssociationUpdateInfo.CreateInsert(new RtEntityId(userType, user.RtId), new RtEntityId(roleType, role.RtId),
                new RtCkId<CkAssociationRoleId>(AssignedRoleId))], apply);
        if (apply.HasErrors || apply.HasFatalErrors)
        {
            throw new InvalidOperationException("Seeding the probe user failed: " +
                                                string.Join(", ", apply.Messages.Select(m => m.MessageText)));
        }

        await session.CommitTransactionAsync();
        ProbeUserRtId = user.RtId.ToString();
        ProbeRoleRtId = role.RtId.ToString();
    }

    /// <summary>
    ///     Stores a <c>System/SimpleRtQuery</c> on the user type - written through the repository, i.e. the way an import
    ///     or any other non-GraphQL writer stores one.
    /// </summary>
    public async Task<string> CreateUserQueryAsync(string[] columns, (string Path, string Value)? filter = null)
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        var queryType = new RtCkId<CkTypeId>("System/SimpleRtQuery");
        var query = await repository.CreateTransientRtEntityByRtCkIdAsync(queryType);
        query.SetAttributeValue("Name", AttributeValueTypesDto.String, "credential-query-" + Guid.NewGuid().ToString("N")[..8]);
        query.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, UserCkTypeId);
        query.SetAttributeValue("Columns", AttributeValueTypesDto.StringArray, columns.ToList());
        if (filter is { } f)
        {
            query.SetAttributeValue("FieldFilter", AttributeValueTypesDto.RecordArray, new List<RtRecord>
            {
                new(new RtCkId<CkRecordId>("System/FieldFilter"), new Dictionary<string, object?>
                {
                    ["AttributePath"] = f.Path,
                    ["Operator"] = 0, // Equals
                    ["ComparisonValue"] = f.Value
                })
            });
        }

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, queryType, query);
        await session.CommitTransactionAsync();
        return query.RtId.ToString();
    }

    /// <summary>Number of captured log lines so far.</summary>
    public int LogCount
    {
        get
        {
            lock (Logs)
            {
                return Logs.Count;
            }
        }
    }

    /// <summary>The log lines captured after <paramref name="start" /> that contain a dummy value (should be empty).</summary>
    public IReadOnlyList<string> LogLinesWithDummyValuesSince(int start)
    {
        lock (Logs)
        {
            return Logs.Skip(start).Where(l => DummyValues.Any(d => l.Contains(d, StringComparison.Ordinal))).ToList();
        }
    }

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add($"{formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
