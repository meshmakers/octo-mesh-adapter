using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     Base class for every node test whose node opens a repository session (AB#5028).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Before AB#5028 every test class built its own
///         <c>A.Fake&lt;ITenantRepository&gt;()</c>. That fake does not implement
///         <see cref="ISecureSessionFactory" />, and
///         <see cref="TenantRepositorySecurityExtensions.GetSessionAsync(ITenantRepository, RtSecurityContext)" />
///         falls back to the parameterless system session <b>silently</b> for a repository that does
///         not — so every caller-scoped call site looked correct while enforcing nothing, and the
///         caller-scoped branch AB#4975 added to <c>ApplyChanges@2</c> was never actually covered.
///         <c>o.Implements&lt;ISecureSessionFactory&gt;()</c> is what closes that hole: the extension
///         now dispatches into a face the test can observe.
///     </para>
///     <para>
///         <b>The two guards.</b> The parameterless <c>GetSessionAsync()</c> / <c>GetSession()</c>
///         throw — production code must reach the repository through <see cref="IMeshEtlContext" />,
///         never directly — and a session opened with <see cref="RtSecurityContext.System" /> throws
///         too, until a test declares that its node is system <i>by classification</i> via
///         <see cref="GivenSystemSessionIsExpected" />. So "this node runs unrestricted" is a visible,
///         reviewable statement in the test rather than the default nobody had to think about.
///     </para>
///     <para>
///         Modelled on <c>octo-mcp-service/tests/McpServices.Tests/TestBase.cs</c>, which solved the
///         same trap for the MCP tools under AB#5030.
///     </para>
/// </remarks>
public abstract class SessionNodeTestBase : NodeTestBase, IDisposable
{
    /// <summary>Subject id the default effective identity acts as.</summary>
    protected const string ScopedSubjectId = "octo-pipeline-sa-test";

    /// <summary>Role the default effective identity carries.</summary>
    protected const string ScopedRole = "PipelineRole";

    private static readonly string ParameterlessSessionMessage =
        "The parameterless session is forbidden in node code (AB#5028). A node must decide explicitly "
        + "whether it acts as the execution's identity (IMeshEtlContext.GetScopedSessionAsync) or "
        + "deliberately as the system context (IMeshEtlContext.GetSystemSessionAsync). "
        + "TenantRepositorySecurityExtensions falls back to this overload SILENTLY, so reaching it "
        + "means the decision was never made.";

    private static readonly string UndeclaredSystemSessionMessage =
        "This node opened a SYSTEM session, which bypasses data-level permissions (AB#4969). If that "
        + "is correct for this node, say so in the test by calling GivenSystemSessionIsExpected() and "
        + "make sure the call site carries the AB#5028 comment explaining what breaks when it is "
        + "scoped. If it is not, the node should use IMeshEtlContext.GetScopedSessionAsync().";

    /// <summary>The faked ETL context every node under test receives.</summary>
    protected IMeshEtlContext EtlContext { get; }

    /// <summary>
    ///     The faked tenant repository, built with an <see cref="ISecureSessionFactory" /> face so the
    ///     security-context overloads are observable.
    /// </summary>
    protected ITenantRepository TenantRepository { get; }

    /// <summary>
    ///     The same object as <see cref="TenantRepository" />, seen through
    ///     <see cref="ISecureSessionFactory" />. Verify against this to assert which
    ///     <see cref="RtSecurityContext" /> a node actually opened its session with.
    /// </summary>
    protected ISecureSessionFactory SecureSessionFactory { get; }

    /// <summary>The session handed out for every accepted session request.</summary>
    protected IOctoSession Session { get; }

    /// <summary>
    ///     The identity <see cref="IMeshEtlContext.GetScopedSessionAsync" /> resolves to. Settable so a
    ///     test can model a different caller; the production resolution itself is covered by
    ///     <c>PipelineIdentityResolverTests</c>.
    /// </summary>
    protected RtSecurityContext EffectiveSecurityContext { get; set; } =
        RtSecurityContext.ForUser(ScopedSubjectId, [ScopedRole]);

    /// <summary>Sets up the fakes and arms both guards.</summary>
    protected SessionNodeTestBase()
    {
        EtlContext = A.Fake<IMeshEtlContext>();
        TenantRepository = A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());
        SecureSessionFactory = (ISecureSessionFactory)TenantRepository;
        Session = A.Fake<IOctoSession>();

        A.CallTo(() => EtlContext.TenantRepository).Returns(TenantRepository);

        // Accepted by default: a session opened for a concrete identity.
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>._))
            .Returns(Task.FromResult(Session));
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>._)).Returns(Session);

        // Guard 1 — the silent fallback. Nothing in production reaches these any more.
        A.CallTo(() => TenantRepository.GetSessionAsync())
            .Throws(() => new InvalidOperationException(ParameterlessSessionMessage));
        A.CallTo(() => TenantRepository.GetSession())
            .Throws(() => new InvalidOperationException(ParameterlessSessionMessage));

        // Guard 2 — the system context needs an explicit opt-in. Configured AFTER the permissive
        // rule above, and FakeItEasy lets the later configuration win for matching calls.
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .Throws(() => new InvalidOperationException(UndeclaredSystemSessionMessage));
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .Throws(() => new InvalidOperationException(UndeclaredSystemSessionMessage));

        // The context's own routing, mirroring MeshEtlContext one for one so a test exercises the
        // same two-step (context decides the identity, extension dispatches to the factory) that
        // production does.
        A.CallTo(() => EtlContext.GetScopedSessionAsync())
            .ReturnsLazily(() => TenantRepository.GetSessionAsync(EffectiveSecurityContext));
        A.CallTo(() => EtlContext.GetScopedSession())
            .ReturnsLazily(() => TenantRepository.GetSession(EffectiveSecurityContext));
        A.CallTo(() => EtlContext.GetSystemSessionAsync())
            .ReturnsLazily(() => TenantRepository.GetSessionAsync(RtSecurityContext.System));
        A.CallTo(() => EtlContext.GetSystemSession())
            .ReturnsLazily(() => TenantRepository.GetSession(RtSecurityContext.System));
    }

    /// <summary>
    ///     Declares that the node under test is <b>system by classification</b> and lifts guard 2.
    ///     Call it from the constructor of a test class whose node legitimately runs unrestricted.
    /// </summary>
    protected void GivenSystemSessionIsExpected()
    {
        GivenSystemSessionIsExpected(Session);
    }

    /// <summary>
    ///     <see cref="GivenSystemSessionIsExpected()" /> handing out a specific session.
    /// </summary>
    /// <param name="session">Session the system request resolves to.</param>
    protected void GivenSystemSessionIsExpected(IOctoSession session)
    {
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .Returns(Task.FromResult(session));
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .Returns(session);
    }

    /// <summary>Makes every accepted scoped session request resolve to <paramref name="session" />.</summary>
    /// <param name="session">Session the scoped request resolves to.</param>
    protected void GivenScopedSessionReturns(IOctoSession session)
    {
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>.That.Matches(c => !c.IsSystem)))
            .Returns(Task.FromResult(session));
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>.That.Matches(c => !c.IsSystem)))
            .Returns(session);
    }

    /// <summary>
    ///     Asserts the node opened its session under the execution's identity — subject and roles
    ///     included, because a context that merely is not the system one would still pass a check on
    ///     <see cref="RtSecurityContext.IsSystem" /> alone.
    /// </summary>
    protected void AssertScopedSessionOpened()
    {
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(
                A<RtSecurityContext>.That.Matches(c => Matches(c, EffectiveSecurityContext))))
            .MustHaveHappened();
    }

    /// <summary>Synchronous counterpart of <see cref="AssertScopedSessionOpened" />.</summary>
    protected void AssertScopedSessionOpenedSynchronously()
    {
        A.CallTo(() => SecureSessionFactory.GetSession(
                A<RtSecurityContext>.That.Matches(c => Matches(c, EffectiveSecurityContext))))
            .MustHaveHappened();
    }

    /// <summary>Asserts the node deliberately opened a system session.</summary>
    protected void AssertSystemSessionOpened()
    {
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .MustHaveHappened();
    }

    /// <summary>Synchronous counterpart of <see cref="AssertSystemSessionOpened" />.</summary>
    protected void AssertSystemSessionOpenedSynchronously()
    {
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>.That.Matches(c => c.IsSystem)))
            .MustHaveHappened();
    }

    /// <summary>Asserts the node opened no session at all, on either factory face.</summary>
    protected void AssertNoSessionOpened()
    {
        A.CallTo(() => SecureSessionFactory.GetSessionAsync(A<RtSecurityContext>._)).MustNotHaveHappened();
        A.CallTo(() => SecureSessionFactory.GetSession(A<RtSecurityContext>._)).MustNotHaveHappened();
        A.CallTo(() => TenantRepository.GetSessionAsync()).MustNotHaveHappened();
        A.CallTo(() => TenantRepository.GetSession()).MustNotHaveHappened();
    }

    /// <summary>
    ///     Runs after every test in every derived class: whichever sessions the node happened to open,
    ///     each caller-scoped one must carry the execution's <b>full</b> identity.
    /// </summary>
    /// <remarks>
    ///     This is what makes "the security context actually arrives" a property of every test rather
    ///     than of the handful that assert it explicitly. Subject and roles are both checked, because
    ///     <see cref="RtSecurityContext.ForUser" /> with a null subject and no roles is not the system
    ///     context and would sail past a check on <see cref="RtSecurityContext.IsSystem" /> alone while
    ///     enforcing nothing. The system direction needs no check here — an undeclared system session
    ///     has already thrown by the time a test gets this far.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        foreach (var call in Fake.GetCalls(SecureSessionFactory))
        {
            if (call.Method.Name is not (nameof(ISecureSessionFactory.GetSessionAsync)
                or nameof(ISecureSessionFactory.GetSession)))
            {
                continue;
            }

            if (call.Arguments[0] is not RtSecurityContext context || context.IsSystem)
            {
                continue;
            }

            Assert.True(Matches(context, EffectiveSecurityContext),
                "A caller-scoped session was opened with "
                + $"subject '{context.SubjectId}' and roles [{string.Join(", ", context.Roles)}], but the "
                + $"execution's identity is subject '{EffectiveSecurityContext.SubjectId}' with roles "
                + $"[{string.Join(", ", EffectiveSecurityContext.Roles)}] (AB#5028).");
        }
    }

    private static bool Matches(RtSecurityContext actual, RtSecurityContext expected)
    {
        return !actual.IsSystem
               && actual.SubjectId == expected.SubjectId
               && actual.Roles.OrderBy(r => r, StringComparer.Ordinal)
                   .SequenceEqual(expected.Roles.OrderBy(r => r, StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
