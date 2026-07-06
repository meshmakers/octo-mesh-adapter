# LlmQuery@1 — Production Readiness Plan

Status: draft · Branch: `dev/philipp/llmquery-node` (octo-mesh-adapter, octo-communication-controller-services, octo-frontend-refinery-studio) · Date: 2026-06-10 · **Auth plan (Phase 2, DD-3/4/5) revised 2026-07-02** after verification against the branch state and the actual code on github.com/meshmakers (octo-mcp-service, comm-controller encrypt-value endpoint, adapter ServiceAccountTokenService).

This document covers (1) the gap analysis of the current branch state, (2) a step-by-step implementation guide to production, including MCP server authentication, streaming responses, and batch processing, and (3) the design decisions with rationale and rejected alternatives (DD-1 … DD-9).

---

## 1. Current state (branch audit)

| Repo | Commits | Adds | State |
|---|---|---|---|
| octo-mesh-adapter | 9 (AB#4072, AB#4102, AB#4140) | `LlmQuery@1` node (~800 LoC): OpenAI-compatible + Anthropic-native via MEAI `IChatClient`, MCP integration (Stdio/Sse/Http), OTel spans, tool-call logging (uncommitted) | Functionally complete v0.1 |
| octo-communication-controller-services | 2 (AB#4140) | `McpConfiguration` CK type (Url, Transport, Command, Arguments, BearerToken) + `McpTransport` enum, model 3.22.0 → 3.23.0 | Complete except migration meta + secret flag |
| octo-frontend-refinery-studio | 7 (AB#4140) | MCP configuration CRUD UI, GraphQL operations, refreshed schema.graphql | Complete except codegen cleanup |

End-to-end smoke verified 2026-06-10: Studio-saved MCP config → `Uses` association → GlobalConfiguration → stdio `uvx mcp-server-time` → tool round via Ollama/nemotron → correct answer (1341 input tokens confirm tool schemas + second round).

## 2. Production gaps

### Blocking

1. **CK migration metadata mismatch** — `ckModel.yaml` declares `System.Communication-3.23.0` (bump uncommitted), `migrations/migration-meta.yaml` still says `ckModelId: System.Communication-3.1.1`. The new type is additive (no data transform needed), but the meta must be updated and the version bump committed, or tenant upgrades will be inconsistent across environments.
2. **Secrets stored and shipped in plaintext** — `McpConfiguration.BearerToken`, `AdditionalHeaders[].Value` (despite the CK description already *claiming* IsSecret values are encrypted — docs are ahead of implementation), `AiConfiguration.ApiKey`, and `ServiceAccountConfiguration.ClientSecret` are all plain String attributes, stored raw in Mongo and shipped raw to the adapter via `AdapterService.CreatePipelineConfigurationAsync` (`c.Serialize()`). See Phase 2 / DD-3.
3. **Frontend codegen debt** — `globalTypes.ts` lines ~136–158 contain hand-written "Temporary" MCP types (commit `b42e987`); `schema.graphql` has since been refreshed and *does* contain McpConfiguration, so `npm run codegen` can now replace them. The generated-looking operation `.ts` files must be regenerated, not hand-maintained.
4. **Zero unit tests** — only two smoke tests exist (`LlmQuerySmokeTests` requires Ollama, `LlmQueryAnthropicSmokeTests` requires `ANTHROPIC_API_KEY`), both effectively manual. No coverage for config validation, JSON-extraction fallback, MCP server resolution failures, transport factories, timeout/cancellation.
5. **Untracked files** — smoke-test pipeline YAML and the new `LogToolCalls` change are uncommitted/untracked.

### Important, non-blocking

6. **OpenAI-path timeout ceiling** — `TimeoutSeconds > 100` is silently capped by the OpenAI SDK's internal HttpClient (documented in `LlmQueryNodeConfiguration` line ~138, "Phase D custom transport" never built). Either build the custom transport or clamp + warn in `Validate()`.
7. **No transport-dependent validation** — Stdio requires Command, Sse/Http require Url; enforced only at execution time with runtime errors. Should fail at deploy time (and in the Studio form).
8. **`AnthropicAiQuery@1` overlap** — the legacy node uses a hand-rolled HTTP MCP client and duplicates LlmQuery functionality. Update 2026-07-06 (AB#4315): it now *does* authenticate — `McpServiceAccountConfigName` on the node config acquires a client-credentials bearer via `ServiceAccountTokenService` and sends it on MCP calls. Caveats of that implementation: node-level (not per-MCP-server) auth, and `EnsureMcpAccessTokenAsync` writes the MCP token into the adapter-global `IServiceClientAccessToken`, clobbering whatever token the adapter's own SDK calls use — a keyed provider avoids both (Phase 2 step 4). Deprecation path still applies (DD-9); the branch must **rebase onto main** to pick these changes up.
9. **MCP v0.1 functional limits**: no Stdio `EnvironmentVariables`, no `WorkingDirectory`, no OAuth. (`AdditionalHeaders` is **done** since AB#4140 — RecordArray of `HttpHeader {Name, Value, IsSecret}` end-to-end: CK type, resolver, Studio form. DD-5 updated accordingly.)
9a. **Stdio `Command` is remote code execution by configuration** — anyone permitted to write `McpConfiguration` entities can spawn arbitrary executables on the adapter host. Acceptable only if that permission is admin-scoped; needs an explicit line in the security review and the runbook, and ideally an allowlist or a per-adapter opt-in flag for Stdio transport.
10. **Operational docs** — no runbook (the troubleshooting knowledge from the 2026-06-10 debugging session lives only in the Studio README; the adapter-side route prefix `/{tenantId}{path}`, `Uses`-association requirement, and stale-process pitfalls should be documented here too).

---

## 3. Step-by-step implementation guide

### Phase 1 — Hardening (1–2 weeks, prerequisite for everything else)

1. Commit the dangling work: `ckModel.yaml` 3.23.0 bump, `LogToolCalls`, smoke-test YAML.
2. Update `migration-meta.yaml` to `ckModelId: System.Communication-3.23.0`. No migration script needed (purely additive type), but add a changelog entry; verify a 3.22→3.23 tenant update on a scratch tenant.
3. Frontend: re-run introspection against a tenant with 3.23.0, `npm run codegen`, delete the temporary hand-written types in `globalTypes.ts`, commit regenerated operations. Add the codegen check to CI (build fails if `.graphql` documents don't validate against `schema.graphql`).
4. Add `Validate()` to `LlmQueryNodeConfiguration` (or the node's startup path): Temperature/TopP mutual exclusion (exists), Stdio↔Command / Http|Sse↔Url consistency *per referenced McpConfiguration at registration time*, `TimeoutSeconds` clamp to 100 for `OpenAiCompatible` with a warning (until item 6 in §2 is fixed properly).
5. Unit tests (no network, all fakeable):
   - `ResolveMcpServers`: name not found → warning + skip; transport as int and as string; missing url/command → typed exception.
   - `ProcessResponse` / `ExtractJsonFromText`: clean JSON, prose-wrapped JSON, malformed, empty.
   - Context building: missing `Path`, `DataPaths`, conversation history shapes.
   - `LogToolCalls`: calls with/without results, unserializable arguments.
   - Mark the two existing smoke tests with traits already present; add a CI filter `Category!=RequiresOllama&Category!=RequiresAnthropic` and a nightly job that *does* run them against Ollama in a container.
6. Mirror transport-dependent validation in the Studio MCP form (disable irrelevant fields — partially done — plus submit-side validation and mutation-error display).
7. Write the runbook section (§5 of this doc) into `docs/` and link from the node's XML docs.

### Phase 2 — Authenticated MCP servers (2–3 weeks, revised 2026-07-02)

Current state, **verified against the branch and github.com/meshmakers**:

- `BuildHttpTransport` already composes `Authorization: Bearer {BearerToken}` **plus** `AdditionalHeaders` (RecordArray of `HttpHeader {Name, Value, IsSecret}` — CK 3.23.0, `McpServerResolver.ParseHeaders`, Studio form). The original step 3 ("custom headers") is **done**; DD-5 rewritten to match.
- The encrypt-on-write precedent is **client-driven, not server-hooked**: the Studio calls the existing `POST /encrypt-value` endpoint (`CommunicationController`, `TenantCommunicationApiReadWritePolicy`) and stores the returned `enc:v1:` blob via the regular GraphQL mutations. There is no server-side mutation hook for `McpConfiguration` — it is written through the *generic* runtime CK mutations, which are secret-agnostic. The previous wording ("encrypt on write in the configuration mutation path") had no implementation point.
- Decrypt must happen **controller-side, not in the adapter**. Precedent: `PoolService` decrypts secret `ValueOverride`s before they go on the SignalR wire. The hook is `AdapterService.CreatePipelineConfigurationAsync`, which today ships configuration entities raw (`c.Serialize()`). Adapters may run on customer premises / at the edge — distributing `InstanceSecretKey` to them would break the trust boundary. The previous wording ("decrypt in the adapter just before transport construction") is therefore wrong.
- `octo-mcp-service` (verified from the public repo: `Program.cs`, `McpSessionTokenStore`, `TenantResolutionService`, `OctoServiceClientFactory`) authenticates **only** via the OAuth2 Device Authorization Flow: tokens live in an in-memory store keyed by MCP session id and are seeded exclusively by the `authenticate` / `check_auth_status` tools. Tool calls never read the HTTP `Authorization` header; `ConfigureJwtBearerOptions` is registered but does not gate the `/mcp` endpoints. **A client-credentials bearer sent by the adapter is silently ignored today.** DD-4 gains a mandatory service-side work item.
- The adapter's `ServiceAccountTokenService` exists and performs the client-credentials grant correctly (discovery, `acr_values` tenant, 60 s buffer) — but it mutates the adapter-global `IServiceClientAccessToken` and caches a **single** token per service instance, and reads the `ServiceAccountConfiguration` entity via `ITenantRepository` rather than the pipeline's `GlobalConfiguration`. It cannot serve multiple MCP configurations with different service accounts as-is.
- `System.Communication/ServiceAccountConfiguration` (IssuerUri, ClientId, ClientSecret, TenantId) already exists in the CK model; the identity server already supports client-credentials clients (`add_client_credentials_client`). That half of DD-4 is confirmed feasible.

Steps:

1. **Encrypt at rest, Studio-driven** (DD-3): reuse `POST /encrypt-value` unchanged. The Studio MCP form encrypts `BearerToken` and every `AdditionalHeaders` entry flagged `IsSecret` before issuing the GraphQL mutation; same treatment for `AiConfiguration.ApiKey` and `ServiceAccountConfiguration.ClientSecret` in their forms. No new endpoint, no engine change, no migration (sentinel passes legacy plaintext through). Read-back is inherently safe: GraphQL returns the `enc:v1:` blob; render set/replace-only password fields.
2. **Decrypt before shipping** (DD-3): in `AdapterService.CreatePipelineConfigurationAsync`, run type-aware decryption over the serialized entities before building `ConfigurationDto` — `McpConfiguration.BearerToken` + `AdditionalHeaders[].Value`, `AiConfiguration.ApiKey`, `ServiceAccountConfiguration.ClientSecret`. Secrets then travel decrypted on the TLS-protected SignalR wire, exactly like PoolService's Helm secrets. The adapter never sees the key; `McpServerResolver` keeps treating values as resolved plaintext (no adapter change).
3. **Header-bearer support in `octo-mcp-service`** — **DONE upstream (AB#4315, verified on main 2026-07-06)**, and stronger than planned: both `/mcp` endpoints now *require* a valid OAuth2 bearer (`AddAuthentication().AddJwtBearer()` + `MapMcp(...).RequireAuthorization()` + `UseOctoTenantAuthorization`). Client-credentials service tokens (no user `sub` claim) skip the route-tenant claim check by design — that is the sanctioned service-to-service path for the mesh-adapter. **Consequence: unauthenticated MCP calls now fail with 401** — LlmQuery pipelines pointed at octo-mcp-service MUST send a bearer once clusters run the new service version.
4. **Keyed token provider in the adapter** (DD-4): add optional `AuthServiceAccountConfigurationName` to `McpConfiguration`. Implement `IMcpBearerTokenProvider` (or refactor `ServiceAccountTokenService`) that (a) resolves the `ServiceAccountConfiguration` from **GlobalConfiguration** (same idiom as `ResolveApiKey`, honors the `Uses` association), (b) caches tokens **per wellKnownName** with the 60 s expiry buffer, (c) never touches the global `IServiceClientAccessToken`. Precedence in `BuildHttpTransport`: service-account token > static `BearerToken`; an explicit `Authorization` entry in `AdditionalHeaders` still overrides both (documented escape hatch, unchanged).
5. **Stdio env vars**: add `EnvironmentVariables` as a RecordArray of the same `HttpHeader`-shaped record (Name/Value/IsSecret) — consistent with the implemented headers design rather than the abandoned line format — mapped to `StdioClientTransportOptions.EnvironmentVariables`. Unlocks GitHub stdio MCP (`GITHUB_TOKEN`) etc. Secret values ride the same encrypt/decrypt path as steps 1–2.
6. Defer full OAuth (`ClientOAuthOptions`, authorization-code) — unchanged; nothing client-credentials doesn't cover for current targets.
7. Tests: token provider (expired → re-acquire, per-name cache isolation, GlobalConfiguration resolution failure → warning + fall back to BearerToken), encryption round-trip incl. legacy-plaintext passthrough, decrypt-before-ship in `CreatePipelineConfigurationAsync`, header precedence. Integration: pipeline → `octo-mcp-service` on test-2 with a client-credentials service account **after step 3 lands**.

### Phase 3 — Streaming responses (2–3 weeks)

Goal: chatbot-style consumption. Two layers must cooperate — the node and the HTTP edge.

1. Bump MEAI to ≥ 10.5.1 and pin it; regression-test tools+streaming per adapter (known historical bugs: dotnet/extensions #6155, #7306 — Chat Completions path is the safe one).
2. Add to the node a streaming execution path using `IChatClient.GetStreamingResponseAsync(...)`. `UseFunctionInvocation` works mid-stream: it buffers function-call fragments, executes the MCP tool between turns, then resumes streaming — text tokens flow, tool execution appears as a stall. Surface tool activity by inspecting `ChatResponseUpdate.Contents` for `FunctionCallContent`.
3. Expose streaming at the edge via the existing dynamic-route mechanism (DD-6): extend `FromHttpRequest@1` (or add `FromHttpRequestStream@1`) so the node can write **SSE** chunks to the `HttpContext` response instead of buffering the full pipeline result. DTO per event: `{kind: text|toolCall|toolResult|done, text?, toolName?}`. Flush on ~50 ms timer or N chars to limit frame overhead; flow the request-abort `CancellationToken` into the LLM call so a closed tab stops token spend.
4. Conversation state: reuse the existing `ConversationHistoryPath` mechanism; the caller (Studio chatbot UI) holds history and resubmits — the node stays stateless (DD-6).
5. Studio: a minimal chat panel consuming the SSE endpoint (Angular `fetch` + `ReadableStream`; no new SignalR infrastructure needed at this stage).

### Phase 4 — Batch processing (2–4 weeks, independent of Phase 3)

Cost lever: 50 % token discount on OpenAI Batch API and Anthropic Message Batches. Cerebras has no batch product (verified June 2026); Ollama has none (self-hosted, no discount concept); Groq offers an OpenAI-shaped batch API at 50 % off if ever added as a provider.

1. **Do not route through LiteLLM** (DD-7). Its `/v1/batches` does not translate to the Anthropic Batches API (native passthrough only), managed-files is beta, and the OpenAI→Anthropic tool-call translation layer adds fidelity risk. Direct vendor APIs are ~3 endpoints each.
2. Implement `IBatchJobService` in MeshAdapter.Sdk with two providers:
   - **OpenAI-shaped** (OpenAI, Azure OpenAI, Groq): build JSONL keyed by `custom_id` → `POST /v1/files (purpose=batch)` → `POST /v1/batches (completion_window=24h)` → poll → download output/error files.
   - **Anthropic**: `POST /v1/messages/batches` with inline `requests[]` → poll `processing_status` → stream `results_url` JSONL. Tools, vision, and prompt caching are batchable; caching discount stacks with the batch discount.
3. Split the pipeline surface into two nodes (DD-8): `LlmBatchSubmit@1` (transform: collects items from the data context, submits, persists a `LlmBatchJob` runtime entity with provider job id + custom_id↔entity mapping) and `FromLlmBatchResult@1` (trigger: a poller — interval timer per pending `LlmBatchJob` — that fires the downstream pipeline once per completed item, or once with all results). Batch jobs survive adapter restarts because state is a CK runtime entity, consistent with the platform's "all persistent data as CK instances" rule.
4. Reuse `LlmQueryNodeConfiguration`'s Connection/AI groups for the submit node; `mcpConfigurationNames` is rejected on the OpenAI batch path (no tool loop inside a batch executor on our side) and allowed for Anthropic (their batch executes tool-use server-side only for server tools — practical rule: MCP tools and batch are mutually exclusive in v1).
5. Cost guardrail: log submitted/completed token counts per job; expose on the `LlmBatchJob` entity for Studio display.

### Phase 5 — Release

1. Deprecate `AnthropicAiQuery@1` (DD-9): mark obsolete in node descriptor + docs, migration note (`provider: Anthropic` equivalent config), removal in the next major CK model bump.
2. Nightly E2E matrix (the seven-row test matrix from the 2026-06-10 session: Ollama/Cerebras/Anthropic × stdio/http/sse/bearer/multi-server).
3. Squash-merge the three branches behind one AB story; tag the CK model release; update docs.meshmakers.cloud (Communication → Pipeline nodes) — the public docs currently say nothing about LlmQuery or MCP.

---

## 4. Design decisions

### DD-1 · Provider abstraction: Microsoft.Extensions.AI `IChatClient`

- **Decision**: One node, one MEAI pipeline (`AsIChatClient` → `UseOpenTelemetry` → `UseFunctionInvocation`), two client factories (OpenAI SDK for all OpenAI-compatible backends, official Anthropic SDK natively).
- **Rationale**: Single tool-call loop and telemetry surface across providers; MCP tools (`McpClientTool : AIFunction`) plug into both paths identically; new providers are a factory, not a node.
- **Alternatives considered**: Per-provider nodes (the `AnthropicAiQuery@1` path — duplicates logic, already caused drift); LangChain-style orchestration frameworks (heavyweight, non-.NET-idiomatic); routing everything through an OpenAI-compatible proxy (loses Anthropic-native features: fine-grained tool use, prompt caching control, no 100 s ceiling).

### DD-2 · MCP configuration as a CK `Configuration` entity, referenced by wellKnownName

- **Decision**: `System.Communication/McpConfiguration` derived from `${System}/Configuration`; pipelines reference it via `mcpConfigurationNames` and must hold a `Uses` association so it ships in `GlobalConfiguration`.
- **Rationale**: Same lifecycle, tooling, GraphQL, and audit as every other configuration; secrets stay out of pipeline YAML; one MCP server is reusable by many pipelines.
- **Alternatives considered**: Inline MCP settings in the node YAML (no reuse, tokens in YAML, rejected); a dedicated non-CK store (violates the "all persistent data as CK instances" platform rule).
- **Known footgun (accept + document)**: a missing `Uses` association degrades silently to a warning. Mitigation: deploy-time validation in Phase 1 step 4 — registration fails if a referenced name is not in the pipeline's configuration set.

### DD-3 · Secret-at-rest: `enc:v1` envelope encryption, not a vault (revised 2026-07-02)

- **Decision**: Encrypt secrets (`McpConfiguration.BearerToken`, `AdditionalHeaders[].Value` where `IsSecret`, `AiConfiguration.ApiKey`, `ServiceAccountConfiguration.ClientSecret`) with the existing `WorkloadEncryptionService` — AES-256-GCM, `enc:v1:` sentinel, key from `InstanceSecretKey`. **Encrypt client-side in the Studio via the existing `POST /encrypt-value` endpoint** (the proven `ValueOverride.IsSecret` flow); **decrypt controller-side in `AdapterService.CreatePipelineConfigurationAsync`** before the configuration ships to the adapter (the proven `PoolService` decrypt-before-wire flow). The adapter never holds the key.
- **Rationale**: Both halves reuse endpoints and services that exist today — this is wiring, not building. `McpConfiguration` is written through *generic* CK mutations, so there is no server-side "mutation path" to hook encryption into; the Studio-driven encrypt-value call is the only write-side hook the platform actually has. On the read side, adapters may run at the edge / on customer premises, so `InstanceSecretKey` must stay inside the cluster — decrypt-before-ship is the only placement consistent with that trust boundary.
- **Alternatives considered**: Decrypt in the adapter (previous plan wording — rejected: requires key distribution to edge adapters); encrypt server-side in a dedicated McpConfiguration mutation (rejected: no such mutation exists and adding one forks the generic-CRUD write path); external vault (operationally heavier, inconsistent with precedent); CK-level generic `isSecret` flag with engine-side encryption — still the *right* long-term home, engine backlog; doing nothing — fails tenant-isolation expectations.
- **Accepted residual**: secrets travel decrypted (TLS-protected) on the SignalR wire and live decrypted in adapter memory during execution — identical to the existing Helm-secret exposure, no new surface.

### DD-4 · Expiring-token auth via `ServiceAccountConfiguration` indirection (revised 2026-07-02)

- **Decision**: Optional `AuthServiceAccountConfigurationName` on `McpConfiguration`; a new **keyed** `IMcpBearerTokenProvider` in the adapter mints and caches one client-credentials bearer **per referenced service account** (60 s expiry buffer), resolved from GlobalConfiguration. Static `BearerToken` remains for third-party servers with long-lived tokens (GitHub PAT). **Prerequisite work item on `octo-mcp-service`: accept `Authorization: Bearer <JWT>` on the `/mcp` endpoints** (validate via its already-registered JwtBearer options; client contexts fall back to the header token when no device-flow session token exists).
- **Rationale**: Update 2026-07-06 — **the server-side prerequisite landed upstream as AB#4315**: octo-mcp-service now requires JWT bearer on the MCP transport, client-credentials tokens skip the tenant-claim check (service-to-service by design), and the legacy `AnthropicAiQuery@1` node demonstrates the end-to-end flow (`McpServiceAccountConfigName` → `ServiceAccountTokenService` → `Authorization: Bearer`). What remains for LlmQuery is only the adapter-side integration — and the upstream node-level pattern should **not** be copied verbatim: (a) LlmQuery composes *multiple* MCP servers per node, so auth belongs on the `McpConfiguration` entity, not the node config; (b) `ServiceAccountTokenService` mutates the adapter-global `IServiceClientAccessToken` and caches a single token per instance — two different service accounts in one adapter process would clobber each other and the adapter's own service identity. Extract the proven grant mechanics (discovery, `acr_values`, 60 s buffer) into the keyed per-wellKnownName provider of Phase 2 step 4. The `ServiceAccountConfiguration` CK type and identity-server support need no change.
- **Alternatives considered**: Storing refresh tokens on McpConfiguration (custodial refresh-token handling, worse blast radius); MCP SDK `ClientOAuthOptions` authorization-code flow (interactive consent doesn't fit unattended pipelines); reusing the adapter's *own* service identity for all MCP calls (no per-configuration identity, over-privileged, poor audit); teaching the pipeline to drive the device flow (absurd for unattended execution — rejected on inspection of the actual service).

### DD-5 · Custom headers / env vars as `HttpHeader` RecordArray (revised 2026-07-02 — decision inverted by implementation)

- **Decision**: `AdditionalHeaders` as a **RecordArray of `HttpHeader {Name, Value, IsSecret}`** — this is what AB#4140 actually shipped (CK record + attribute, `McpServerResolver.ParseHeaders`, Studio form), superseding the line-format design this DD originally chose. `EnvironmentVariables` (still open) follows the same record shape for consistency. Both map to the SDK's `HttpClientTransportOptions.AdditionalHeaders` / `StdioClientTransportOptions.EnvironmentVariables`; an explicit `Authorization` entry overrides the `BearerToken`-derived header.
- **Rationale**: The per-entry `IsSecret` flag turned out to be load-bearing, not optional — it is what the DD-3 encryption slice keys on (encrypt exactly the flagged values in the Studio, decrypt exactly those in `CreatePipelineConfigurationAsync`). The line format had no place to carry that flag. Mirrors the `ValueOverride` pattern, so the Studio form and encryption handling are copies of an existing implementation rather than new design.
- **Alternatives considered**: Line-formatted `Name: Value` string (the original decision here — rejected in implementation: no per-entry secrecy, and parsing secrets out of a blob string makes partial encryption ambiguous); JSON-blob attribute (opaque in Studio forms).

### DD-6 · Streaming: SSE over the existing dynamic-route middleware; node stays stateless

- **Decision**: Stream via Server-Sent Events written directly to the `HttpContext` of the dynamic route (`/{tenantId}{path}`), fed by `GetStreamingResponseAsync`; conversation history remains caller-supplied via `ConversationHistoryPath`.
- **Rationale**: SSE needs no new infrastructure (the `DynamicRouteMiddleware` already owns the response); `UseFunctionInvocation` streams correctly (buffers tool-call fragments, executes, resumes); a stateless node keeps pipeline semantics — state lives with the caller or in CK entities, never in the adapter process. Cancellation propagates naturally from request abort to the provider call.
- **Alternatives considered**: SignalR hub on the adapter (better for fan-out/multi-client, but new endpoint surface, auth story, and client lib for a single-consumer chatbot — adopt later if Studio needs multi-session push; the documented hub-streaming pattern with `IAsyncEnumerable` + `[EnumeratorCancellation]` is the upgrade path); WebSockets (overkill, no server→client-only need); buffering with chunked polling (defeats the purpose).
- **Known constraint**: streamed responses bypass `ProcessResponse`/JSON extraction — streaming mode forces `responseFormat: text` and skips `targetPath` writes except a final aggregate (validate this combination).

### DD-7 · Batch: direct vendor APIs, no LiteLLM

- **Decision**: Implement OpenAI Batch (file-based JSONL) and Anthropic Message Batches (inline JSON) directly behind an `IBatchJobService`; skip LiteLLM.
- **Rationale**: Both vendor flows are ~3 stable endpoints with 50 % discounts; LiteLLM's `/v1/batches` covers OpenAI/Azure/Vertex/Bedrock/vLLM but does **not** translate to Anthropic's batch API (native passthrough only), its managed-files feature is beta, its OpenAI→Anthropic tool translation has a bug history, and it adds a proxy deployment + new failure domain to every request. We already maintain a native Anthropic path — funneling it through an OpenAI-format proxy would *lose* fidelity.
- **Alternatives considered**: LiteLLM proxy (adopt only if centralized multi-team key/budget governance becomes a requirement — that is its real differentiator, not batch); OpenRouter (no batch product, no discount); waiting for an MEAI batch abstraction (none exists; `Microsoft.Extensions.AI.Evaluation` is a consumer of results, not a batch engine).

### DD-8 · Batch pipeline shape: submit node + result trigger, state as CK entity

- **Decision**: `LlmBatchSubmit@1` (transform) persists an `LlmBatchJob` runtime entity (provider, job id, custom_id↔item map, token counts); `FromLlmBatchResult@1` (trigger) polls pending jobs and fires the continuation pipeline with results.
- **Rationale**: Batch completion windows are minutes-to-24 h — a synchronous node blocking a pipeline execution that long is operationally absurd (timeouts, restarts, no observability). Two pipelines model the async boundary honestly; CK-entity state survives adapter restarts and is visible/auditable in Studio like everything else.
- **Alternatives considered**: Blocking poll inside one node (rejected, above); external job runner outside the pipeline system (splits the mental model, loses pipeline statistics/debugging); RabbitMQ-based completion events (the poller can publish one later — start with polling, it's the only mechanism both vendors support anyway).
- **Scope rule**: MCP tools + batch are mutually exclusive in v1 (no client-side tool loop exists inside vendor batch executors).

### DD-9 · Deprecate `AnthropicAiQuery@1`

- **Decision**: Freeze, mark obsolete, document the `LlmQuery@1` equivalent (`provider: Anthropic`), remove at the next major CK model version.
- **Rationale**: It duplicates LlmQuery with less capability (hand-rolled MCP HTTP client, *no* MCP auth at all — only `Mcp-Session-Id` — single server, Anthropic-only) and every fix now has to land twice.
- **Alternatives considered**: Keeping both indefinitely (drift, double maintenance — already observable); silent alias/rewrite to LlmQuery (surprising config semantics; explicit migration is cheaper than debugging implicit translation).

---

## 5. Operational runbook (condensed)

- Trigger URLs are tenant-prefixed: `https://{adapter}:5020/{tenantId}{path}` — the path in the YAML is *not* the full route.
- A pipeline reaches the adapter only when: `Enabled`, `Executes` → the adapter rtId the process registered as (`OCTO_ADAPTER__ADAPTERRTID`), and deployed. Watch for `Registering pipeline … {rtId}` on adapter startup; deployment errors land on the *adapter* entity (`lastConfigurationError`), not always on the pipeline.
- Every name in `mcpConfigurationNames` and `apiKeyConfigurationName` must match an entity `rtWellKnownName` (case-insensitive) **and** be linked to the pipeline via `Uses`, or the node logs a warning and silently runs without tools.
- Stdio MCP servers spawn with the adapter process environment — IDE-launched adapters often miss `~/.local/bin`; use absolute `Command` paths when in doubt.
- **Security**: Stdio `Command` is arbitrary code execution on the adapter host. Restrict `McpConfiguration` write permission to admins; treat any new Stdio config as a change requiring review.
- Since AB#4315, `octo-mcp-service` **requires** a bearer token on every MCP request (401 otherwise). Pipelines authenticate via a client-credentials service account; interactive clients additionally run the device flow for family-1 (SDK-backed) tools.
- Tool invocation visibility: per-execution log lines from `LogToolCalls` (Info: name+args, Debug: truncated results) and `gen_ai.*` OTel spans (`ActivitySourceName = Meshmakers.Octo.Sdk.MeshAdapter.LlmQuery`).
- Token-count heuristic: input tokens ≫ prompt size ⇒ tool schemas + multi-round loop (e.g. 177-tool `octo-mcp-service` adds tens of thousands of input tokens — don't point small local models at it).
- OpenAI-compatible path: effective timeout ceiling 100 s until the custom transport lands; Anthropic path has no ceiling.

## 6. Effort summary

| Phase | Content | Estimate |
|---|---|---|
| 1 | Hardening: migration meta, codegen, validation, unit tests, runbook | 1–2 w |
| 2 | MCP auth: Studio-side encryption, controller decrypt-before-ship, octo-mcp-service header-bearer, keyed token provider, env vars (headers already done) | 2–3 w |
| 3 | Streaming: MEAI bump, SSE edge, Studio chat panel | 2–3 w |
| 4 | Batch: IBatchJobService, submit/trigger nodes, job entity | 2–4 w |
| 5 | Deprecation, E2E matrix, docs, merge | 1 w |

Phases 3 and 4 are independent of each other; both depend on Phase 1. Phase 2 is required before any production use of authenticated MCP servers (including `octo-mcp-service` on test-2).

---

## 7. Migration map to current main (added 2026-07-06)

**Finding**: the `main` histories of octo-mesh-adapter and octo-communication-controller-services were **rewritten** during the communication-SDK split — zero common commits with the local clones (both histories restart at "Initial commit 2024-03-24" under new SHAs). A normal `git rebase` against the old base is meaningless; the branch commits must be **transplanted** onto the new history (`git format-patch` from the old base + `git am -3`, or cherry-pick from the old branch into a new branch off fresh main). Local `origin/*` refs are stale *and* obsolete — re-clone or hard-reset main after fetching, and export the branch (`format-patch`) first.

### octo-mesh-adapter (11 branch commits · new main verified via anonymous fetch)

- Everything the branch **adds** (LlmQueryNode + config, LlmProvider, McpServerResolver, McpToolCallNode + config, integration tests, docs) does not exist on new main → applies cleanly.
- Expected conflicts, all trivial:
  - `MeshAdapter.Sdk.csproj` — main moved to `Meshmakers.Octo.Sdk.Adapters` + `Meshmakers.Octo.Sdk.Pipeline` @ `$(OctoCommVersion)` (Phase 3 SDK cut), IronOcr 2026.6.1, added MailKit / SSH.NET / Services.Notifications / Runtime.Engine.MongoDb+CrateDb. Resolution: take main's file, re-add the four LLM packages (`Anthropic` 12.24.1, `Microsoft.Extensions.AI` + `.OpenAI` 10.6.0, `ModelContextProtocol` 1.4.0).
  - `ServiceCollectionExtensions.cs` / `DataPipelineBuilderExtensions.cs` — main added Export/ImportDataPointMappings registrations adjacent to our LlmQuery/McpToolCall lines.
- **No source edits needed for the SDK split**: the cut kept namespaces (`Meshmakers.Octo.Sdk.Common.EtlDataPipeline` etc. now ship in `Sdk.Pipeline`); verified against main's `AnthropicAiQueryNode`.
- Main gained AB#4315 (`McpServiceAccountConfigName` on the legacy node) and AB#4316 (array-aware `ExtractJsonFromText` — worth porting into LlmQuery's copy). `ServiceAccountTokenService` is registered as **singleton** on main — confirms the keyed-provider requirement in DD-4.
- Local `DebugL` builds: the shared `../nuget` feed has no `Sdk.Pipeline` / `Sdk.Adapters` 999.0.0 packages yet — build `octo-communication-sdk` in DebugL first to populate the feed.

### octo-communication-controller-services (3 branch commits · new main verified)

- Main-side CK drift since the old base is tiny: ckModel 3.22.0 → **3.24.0** (AiModel made mandatory without default; blueprint entries; csproj version-var rework). No MCP files on main.
- The branch also claims 3.24.0 → **collision**. Re-apply McpConfiguration / HttpHeader / McpTransport as **3.25.0**, update both blueprint.yaml files and migration-meta, and add the `AuthServiceAccountConfigurationName` attribute (Phase 2 step 4) in the same bump — one migration instead of two.
- `POST /encrypt-value` and `WorkloadEncryptionService` are present on new main → Phase 2 steps 1–2 unaffected.

### octo-frontend-refinery-studio (8 branch commits · **private repo, not inspectable from this session**)

- Fetch locally and check whether main touched `schema.graphql`, `globalTypes.ts`, or the configurations routes since 2026-06-09 (old fork point).
- Regardless: **regenerate, don't merge** the codegen artifacts — reapply only the hand-written commits (MCP form component, routes, `.graphql` operation documents), then re-run introspection + `npm run codegen` against a 3.25.0 tenant. This also clears Phase 1 step 3's "temporary global types" debt in the same pass.

### octo-communication-sdk (new upstream dependency)

- No changes needed there; LlmQuery compiles against `Sdk.Pipeline` as-is. It only enters the picture as the DebugL feed prerequisite above.

### Recommended order

1. Fetch/reset all three repos to new main (export branch via format-patch first); build octo-communication-sdk DebugL for the local feed.
2. Transplant the adapter commits (consider squashing to 3–4 logical commits), resolve the two csproj/DI conflicts, port the AB#4316 extraction improvements into `LlmQueryNode.ProcessResponse`.
3. Transplant the CK model as 3.25.0 + `AuthServiceAccountConfigurationName`.
4. Implement the keyed token provider + bearer injection in `McpServerResolver.BuildTransport` (DD-4).
5. Studio: reapply UI commits, regenerate schema/codegen against 3.25.0.
6. E2E smoke incl. authenticated `octo-mcp-service` (client-credentials service account).

