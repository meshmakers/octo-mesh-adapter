# LlmQuery@1 — v1 Plan (production-minimal)

Status: draft · Date: 2026-06-10 · Scope source: `llmquery-production-plan.md` (Phases 1 + encryption item of Phase 2)

**v1 definition**: `LlmQuery@1` with MCP tool support is safe to ship for pipeline use — OpenAI-compatible (Ollama/Cerebras) and Anthropic providers, Stdio/Sse/Http MCP transports, static bearer auth.
**Explicitly out of scope**: streaming, batch, OIDC/service-account MCP auth, custom headers/env vars, `AnthropicAiQuery@1` deprecation, >100 s OpenAI transport fix (clamped instead).

---

## Workstream A — Repo hygiene (½ day)

1. **octo-communication-controller-services**
   - Commit the uncommitted `ckModel.yaml` bump (`System.Communication-3.23.0`).
   - Update `ConstructionKit/migrations/migration-meta.yaml`: `ckModelId: System.Communication-3.23.0`. No migration script required — `McpConfiguration` is purely additive — but the meta must track the current version so the upgrade chain stays coherent.
   - Verify on a scratch tenant: import 3.22.0 → update to 3.23.0 → `systemCommunicationMcpConfigurations` appears in the tenant schema.
2. **octo-mesh-adapter**
   - Commit `LogToolCalls` (LlmQueryNode.cs) and `tests/MeshAdapter.Sdk.IntegrationTests/TestData/llmquery-smoke-pipeline.yaml` (fix the outdated curl comment: routes are tenant-prefixed, `https://localhost:5020/{tenantId}/test/llmquery`).

**Acceptance**: clean `git status` on all three repos; scratch-tenant model update succeeds.

## Workstream B — Frontend codegen cleanup (½ day)

1. `schema.graphql` already contains `McpConfiguration` (commit `3e7f435`) — run `npm run codegen`.
2. Delete the temporary hand-written block in `src/app/graphQL/globalTypes.ts` (~lines 136–158, commit `b42e987`); fix any naming drift the generated types reveal (enum casing `STDIO/SSE/HTTP`, input type names).
3. Commit regenerated operation files (`createMcpConfiguration.ts`, `getMcpConfigurationDetails.ts`, `updateMcpConfiguration.ts`, `possibleTypes.ts`).
4. CI guard: add a pipeline step that runs codegen and fails on diff (protects against future hand-edits; the README troubleshooting section documents the token/introspection workflow).

**Acceptance**: `ng build` green with zero hand-written GraphQL types; MCP config CRUD still works against meshtest.

## Workstream C — BearerToken encryption at rest (2–3 days)

Pattern: reuse the existing `enc:v1` envelope (AES-256-GCM, `WorkloadEncryptionService`, key = `CommunicationControllerOptions.InstanceSecretKey`). All pieces exist; this is wiring.

1. **Studio encrypts on save** — `mcp-configuration-details.component.ts`, `onSave()`:
   - If the user changed the token (`!bearerTokenIsServerValue`) and it is non-empty, call the existing `communicationService.encryptValue(tenantId, token)` (same call the adapters/applications forms use for `IsSecret` overrides; controller endpoint `CommunicationController` → `EncryptValue`, idempotent via `IsEncrypted` guard) and store the returned `enc:v1:` ciphertext in the mutation input.
   - The unchanged-token path already round-trips the stored value verbatim — no change.
2. **Controller decrypts when shipping to the adapter** — `AdapterService.cs` (~line 806, `pipelineConfigurations.Select(c => new ConfigurationDto(...c.Serialize()))`):
   - For entities with `CkTypeId == System.Communication/McpConfiguration`, post-process the serialized JSON: `bearerToken = _encryptionService.Decrypt(bearerToken)`. `Decrypt()` passes non-sentinel values through unchanged → **zero migration**, existing plaintext tokens keep working.
   - Precedent: this is exactly how workload secrets reach the communication operator (`PoolService` decrypts `repo.Password` / `IsSecret` overrides into `WorkloadDeployedDto`). The plaintext exists only in the TLS SignalR channel and adapter memory; the adapter never holds `InstanceSecretKey` (matters for edge deployments).
3. **CK model**: extend the `BearerToken` attribute description to state the value is stored `enc:v1`-encrypted; do not change the type (stays String — sentinel-prefixed ciphertext).
4. **GraphQL read-back**: confirm the entity query returns ciphertext (it will, it's the stored value) — the form already masks it (`••••••••`) and never displays it. Verify no other UI surface (configurations list, generic entity browser) renders the raw attribute; if the generic browser does, accept it for v1 (it shows ciphertext, not the secret).
5. **Tests**: encryption round-trip unit test (controller); form test: changed vs. untouched token paths; adapter integration: pipeline with encrypted token against a bearer-protected MCP server (can be `mcp-server-time` behind a one-line auth proxy, or skip to manual verification against GitHub MCP).

**Acceptance**: new/edited tokens land in Mongo with the `enc:v1:` prefix; a pre-existing plaintext token still works; tool calls against a bearer-auth MCP server succeed; the plaintext never appears in any log (`LogToolCalls` does not log transport config — verify).

## Workstream D — Deploy-time validation (2–3 days)

Goal: turn today's three silent failure modes into registration-time errors surfaced on the adapter/pipeline deployment state.

1. **Configuration-reference validation** (the `Uses`-association footgun): at pipeline registration (`PipelineRegistryService.RegisterPipelineAsync`, after `GlobalConfiguration` is built and the configuration root is deserialized), validate each `LlmQueryNodeConfiguration`:
   - every name in `McpConfigurationNames` is defined in `GlobalConfiguration` (else: `PipelineInitializationError` listing the missing name and the fix — "create the configuration and link it to the pipeline via the Uses association");
   - `ApiKeyConfigurationName`, when set, is defined likewise.
   - Mechanism: introduce `IValidatableNodeConfiguration { void Validate(IGlobalConfiguration cfg); }` in Sdk.Common; the registry walks node configs and invokes it. Generic — future nodes opt in. (Alternative if SDK change is unwanted for v1: do the check in `LlmQueryNode` at first execution and *throw* instead of warning — weaker, deploy still "succeeds", but zero SDK surface. Prefer the SDK hook.)
2. **Shape validation** (same hook, or constructor-level):
   - `Temperature`/`TopP` mutual exclusion (exists at execution time — move/duplicate to registration);
   - `ResponseFormat` ∈ {json, text};
   - `MaxToolRounds` ≥ 1; `TimeoutSeconds` ≥ 1.
3. **Per-MCP-config shape check** (needs `GlobalConfiguration`, so registration-time): Transport=Stdio ⇒ Command non-empty; Transport=Sse|Http ⇒ Url non-empty and absolute `https?://` URI. Mirrors the runtime checks in `BuildStdioTransport`/`BuildHttpTransport` but fails at deploy.
4. **Timeout clamp**: `Provider == OpenAiCompatible && TimeoutSeconds > 100` ⇒ clamp to 100 + registration warning referencing the SDK HttpClient ceiling (full fix deferred to v2).
5. **Studio form**: enforce the same Stdio/Http field requirements as validators (conditional fields exist; add required-validation + mutation-error display on save failure).
6. Deployment errors must be visible: confirm `DeploymentUpdateErrorMessageDto` from a failed validation lands on the adapter entity (`lastConfigurationError`) and is shown in Studio — that was the blind spot on 2026-06-10.

**Acceptance**: deploying a pipeline referencing a missing MCP config fails with an actionable message visible in Studio; Stdio-without-Command fails at deploy; `timeoutSeconds: 300` on Ollama logs a clamp warning at registration.

## Workstream E — Unit-test core (2–3 days, parallel to C/D)

New `LlmQueryNodeTests` (pure, no network) in `MeshAdapter.Sdk.Tests` (create the unit-test project if only IntegrationTests exists):

| Area | Cases |
|---|---|
| `ResolveMcpServers` | name missing → skip + warning; transport as int (0/1/2), as string (case-insensitive), invalid → Sse fallback; bearer/command/url passthrough |
| `BuildStdioTransport` / `BuildHttpTransport` | empty Command/Url → typed exception with config name; argument parsing (one-per-line, JSON array); Http vs Sse `TransportMode` mapping |
| `ProcessResponse` / `ExtractJsonFromText` | clean JSON; prose-wrapped JSON; malformed → fallback to text; empty |
| `LogToolCalls` | no tools offered → silent; offered-but-unused → info line; call+result pairing by CallId; unserializable args; >500-char result truncation |
| Validation (Workstream D) | each rule, positive + negative |
| Config defaults | `Temperature`/`TopP` exclusivity, enum YAML round-trip (`OpenAICompatible` vs `OpenAiCompatible` casing) |

CI: unit tests always; smoke tests stay behind `Category!=RequiresOllama&Category!=RequiresAnthropic`; nightly job runs the Ollama smoke against a container.

**Acceptance**: tests green in CI without network; the two smoke suites unchanged and nightly-scheduled.

---

## Sequencing & estimate

```
A (hygiene) ─┬─► B (codegen)       ─┐
             ├─► C (encryption)     ├─► v1 merge + E2E pass
             └─► D (validation) ────┘
                  E (tests) runs parallel to C/D
```

| Workstream | Effort |
|---|---|
| A Hygiene | 0.5 d |
| B Codegen | 0.5 d |
| C Encryption | 2–3 d |
| D Validation | 2–3 d |
| E Tests | 2–3 d (parallel) |
| **Total** | **~7–9 working days** |

## v1 exit checklist

- [ ] All branch work committed; migration meta at 3.23.0; scratch-tenant update verified
- [ ] Zero hand-written GraphQL types; codegen in CI
- [ ] BearerToken `enc:v1` at rest; legacy plaintext still functional; no plaintext in logs
- [ ] Missing config reference / bad transport shape / oversized timeout fail or warn at deploy, visible in Studio
- [ ] Unit-test core green in CI; nightly Ollama smoke scheduled
- [ ] E2E matrix re-run: Ollama+stdio, Cerebras+stdio, Anthropic+deepwiki(Http), Anthropic+deepwiki(Sse), multi-server, GitHub-PAT bearer (encrypted)
- [ ] Runbook section (tenant-prefixed routes, Uses association, adapter identity, stale-process restart) merged into docs
