{{/*
  Kubernetes EnvVar.value is typed string, so every env value must render as a
  YAML string scalar. Without `| quote`, values that look like YAML scalars of
  another type (numbers, booleans, "null", "yes/no") get interpreted as that
  type and the apiserver rejects the Deployment with
  "cannot unmarshal number into Go struct field EnvVar.value of type string".
  Specifically: blueprint-seeded RtIds like 670000000000000000000002 are 24
  decimal digits and parse as numbers. `| quote` everywhere — env values are
  always strings.
*/}}
{{- define "octo-mesh.system-env" -}}
- name: OCTO_SYSTEM__DATABASEHOST
  value: {{ .Values.clusterDependencies.mongodbHost | quote }}
{{- if .Values.clusterDependencies.systemDatabaseName }}
{{/*
  Instance isolation (Epic AB#4944): the tenant registry lives in this database, and
  the adapter resolves its own tenant through it on every CK-model load. It must match
  the core services' serviceDefaults.systemDatabaseName; an instance on a non-default
  system database otherwise fails with "Tenant '<id>' does not exist". Omitted when
  unset, so a single-instance cluster keeps the adapter's compiled-in default.
*/}}
- name: OCTO_SYSTEM__SYSTEMDATABASENAME
  value: {{ .Values.clusterDependencies.systemDatabaseName | quote }}
{{- end }}
{{- if .Values.clusterDependencies.mongodbReplicaSet }}
- name: OCTO_SYSTEM__REPLICASETNAME
  value: {{ .Values.clusterDependencies.mongodbReplicaSet | quote }}
{{- end }}
{{/*
  🔴 AB#4924 — a pool member gets NEITHER of these, and could not be deployed if it did.

  They are the installation's shared datasource and admin passwords: the datasource user name is a
  format string over the database name, so whoever holds that password can open EVERY tenant's
  database, and the admin password needs no explanation. A pool member executes work for tenants
  other than the one that owns it and is handed exactly one of them at a time by a lease — a standing
  credential to all of them would make that lease decorative. The communication operator refuses to
  hand the cluster-secret tier to an AdapterPool for the same reason (WorkloadReconciler
  .AppendClusterSecrets), so the values would not arrive anyway and `octo-mesh.secretEnv`, which fails
  on an empty value, would fail the render instead.

  What replaces them: the member is TOLD where the leased tenant lives and how to open it
  (ITenantLocationSource + ITenantDatabaseCredentialSource in the runtime engine, both fed from the
  lease), so it never resolves a tenant through the installation's registry and never needs an
  installation-wide credential at all. The host, the system database name and the replica set above
  are not secrets and are still rendered — a connection needs an address either way.
*/}}
{{- if not (include "octo-mesh.isPoolMember" .) }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__DATABASEUSERPASSWORD" "value" .Values.secrets.databaseUser "legacyKey" "databaseUser" "context" .) }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__ADMINUSERPASSWORD" "value" .Values.secrets.databaseAdmin "legacyKey" "databaseAdmin" "context" .) }}
{{- include "octo-mesh.secretEncryption-env" . }}
{{- end }}

{{/*
  AB#5536 — SECRET attribute key ring (concept AB#5528 §3.5), bound by the engine as
  SecretEncryption:Keys:<kid> / :ActiveKeyId / :LegacyV1Key. The communication operator
  supplies it as secrets.secretEncryption.* for workloads with ReceivesClusterSecrets=true
  (keys + legacy key as valueFrom maps into {release}-octo-secrets, the active key id as a
  plain string). Rendered only when provided: an adapter without the ring still starts,
  and only writing or reading a SECRET attribute fails with a configuration error.
  The key id keeps its case in the variable name — it is the id the engine writes into
  the enc:v2:<kid>: header.
*/}}
{{- define "octo-mesh.secretEncryption-env" -}}
{{- $se := .Values.secrets.secretEncryption | default dict }}
{{- $keys := $se.keys | default dict }}
{{- range $kid, $value := $keys }}
{{- if not (regexMatch "^[a-z0-9]{1,32}$" $kid) }}
{{- fail (printf "secrets.secretEncryption.keys: key id '%s' must be 1-32 lowercase letters or digits" $kid) }}
{{- end }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "OCTO_SECRETENCRYPTION__KEYS__%s" $kid) "value" $value "legacyKey" (printf "secretEncryptionKey-%s" $kid) "context" $) }}
{{- end }}
{{- if $keys }}
{{- $active := $se.activeKeyId | default "" }}
{{- if and (not $active) (eq (len $keys) 1) }}
{{- $active = keys $keys | first }}
{{- end }}
{{- if not (hasKey $keys $active) }}
{{- fail (printf "secrets.secretEncryption.activeKeyId '%s' is not a key id of secrets.secretEncryption.keys (%s)" $active (keys $keys | sortAlpha | join ", ")) }}
{{- end }}
- name: OCTO_SECRETENCRYPTION__ACTIVEKEYID
  value: {{ $active | quote }}
{{- end }}
{{- if $se.legacyV1Key }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SECRETENCRYPTION__LEGACYV1KEY" "value" $se.legacyV1Key "legacyKey" "secretEncryptionLegacyV1Key" "context" $) }}
{{- end }}
{{- end }}
{{- end }}

{{- define "octo-mesh.broker-env" -}}
- name: {{ printf "%s__BROKERHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqHost | quote }}
- name: {{ printf "%s__BROKERUSERNAME" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqUser | quote }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__BROKERPASSWORD" (upper .name)) "value" .global.Values.secrets.rabbitmq "legacyKey" "rabbitmq" "context" .global) }}
{{- end }}

{{- define "octo-mesh.streamdata-env" -}}
# Instance-level kill switch for StreamData. Read by
# StreamDataInstanceConfiguration (root "StreamData" config section, hence
# the fixed env-var name without a service prefix). Defaults to false so
# the feature is opt-in per cluster.
- name: OCTO_STREAMDATA__ENABLED
  value: {{ .global.Values.clusterDependencies.streamDataEnabled | quote }}
{{- if .global.Values.clusterDependencies.streamDataSchemaInstancePrefix }}
{{/*
  AB#4946 / Epic AB#4944: prefixes the tenant's CrateDB schema so a second instance
  does not read and write the first one's data. Same root "StreamData" config section
  as the kill switch above, hence no service prefix. Omitted when unset — the legacy,
  unprefixed schema names stay byte-identical.
*/}}
- name: OCTO_STREAMDATA__SCHEMAINSTANCEPREFIX
  value: {{ .global.Values.clusterDependencies.streamDataSchemaInstancePrefix | quote }}
{{- end }}
- name: {{ printf "%s__STREAMDATAHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataHost | quote }}
- name: {{ printf "%s__STREAMDATAUSER" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataUser | quote }}
{{/*
  🔴 AB#4924 — withheld from a pool member, like the two Mongo secrets above, and for
  a reason that cannot be fixed by handing it over: there is NO per-tenant CrateDB
  principal. The engine holds one connection string per installation and separates
  tenants by SCHEMA, so this one credential reaches every tenant's stream data. On a
  lease it would be time-scoped but not tenant-scoped — the shape of the mechanism
  without its substance.

  The accepted consequence (concept §4, AB#4924): a LEASED pipeline that writes an
  archive fails to connect, rather than reaching a neighbour's schema. A per-tenant
  CrateDB principal has to exist before that can change.

  The host and user above are not secrets and stay rendered, so the failure is a
  refused connection with a named user rather than a half-configured process.
*/}}
{{- if not (include "octo-mesh.isPoolMember" .global) }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__STREAMDATAPASSWORD" (upper .name)) "value" .global.Values.secrets.streamDataPassword "legacyKey" "streamDataPassword" "context" .global) }}
{{- end }}
{{- end }}


{{- define "octo-mesh.env" -}}
- name: ASPNETCORE_URLS
  value: "http://+:80"
{{- /*
  AB#5478 §2.1 + §2.2 — per-tenant identity for this workload.

  Both halves of the identity were collapsed across tenants before this. The pod
  label app.kubernetes.io/name is the CHART name, identical for every adapter of
  every tenant in the cluster, and the in-process SDK fell back to the entry
  assembly name, equally shared. So every tenant's adapter arrived in Dash0 as a
  single service — measured on test-2: 127,976 requests at p95 3.19 s with no way
  to tell whose latency that was.

  OTEL_SERVICE_NAME is the release name, which under the operator already reads
  {tenantId}-{workloadName} and is already the value of the
  app.kubernetes.io/service label. Metrics, traces and logs therefore land on the
  SAME name: metrics and traces because container env beats everything the operator
  would otherwise derive, logs because the Dash0Monitoring transform reads that
  label. One value, three signals.

  octo.tenant.id carries the tenant as its own dimension on every span, metric and
  log record of this pod, with no code change. One spelling fleet-wide — the
  engine's own `tenant` / `streamdata.tenant` attributes are being unified onto
  this key, so do not introduce a fourth.

  Guarded on tenantId because the chart defaults it to "" for out-of-band installs;
  an empty value would publish `octo.tenant.id=` as a real, wrong attribute.
*/}}
- name: OTEL_SERVICE_NAME
  value: {{ include "octo-mesh.service-fullname" . | quote }}
{{- if .Values.tenantId }}
- name: OTEL_RESOURCE_ATTRIBUTES
  value: {{ printf "octo.tenant.id=%s" .Values.tenantId | quote }}
{{- end }}
# Our own ActivitySources, declared to the injected .NET auto-instrumentation.
# The adapter hosts the same StreamData engine as the platform services, so the
# same two sources are emitted here — and the same rule applies: the SDK inside
# the process subscribes to them (Meshmakers.Octo.Services.Observability) but
# deliberately carries no trace exporter in the cluster, because a second one
# would duplicate every HTTP span the injector already sends. Naming them here
# is what gets these spans out. Mirrors the identical block in the octo-mesh
# chart; keep the two lists in step.
- name: OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES
  value: "Meshmakers.Octo.StreamData,Meshmakers.Octo.StreamData.Crate"
{{- /*
  AB#5478 section 2.3: the level comes from the environment now, not from a
  hard-coded minlevel="Debug" in the adapter repository. Omitted unless set, and
  nlog.config then falls back to Info.
*/}}
{{- if .Values.logLevel }}
- name: OCTO_LOG_LEVEL
  value: {{ .Values.logLevel | quote }}
{{- end }}
{{- if .Values.logLevelRoot }}
- name: OCTO_LOG_LEVEL_ROOT
  value: {{ .Values.logLevelRoot | quote }}
{{- end }}
{{- $name := "OCTO_ADAPTER" }}
{{ include "octo-mesh.system-env" . }}
{{ include "octo-mesh.broker-env" (dict "global" . "name" $name) }}
{{ include "octo-mesh.streamdata-env" (dict "global" . "name" $name) }}
- name: OCTO_ADAPTER__INSTANCEPREFIX
  value: {{ .Values.instancePrefix | quote }}
{{/*
  AB#4924 — a pool member is a different kind of process, and the difference is
  visible right here.

  `IsEnabled` on AdapterPoolMemberOptions requires BOTH ids, so both are the
  condition; half a configuration must render as "not a pool member" rather than
  as a broken one. A member then gets neither a dedicated tenant nor an adapter
  RtId: it belongs to no tenant until a lease arrives, and it registers through
  the pool hub rather than as an adapter entity. The SDK clears
  AdapterOptions.DedicatedTenantId on a configured member regardless
  (ConfigurePoolMemberAdapterTenantId), because three call sites justify their
  safety with that value being null — but a chart that still rendered a tenant
  would make `kubectl describe pod` say the opposite of what the process does.

  No MEMBERID: AdapterPoolMemberOptions.EffectiveMemberId falls back to the
  machine name, which is the pod name here. A configured value would give every
  replica of the deployment the same member id, and the controller's registry
  keys members by it.
*/}}
{{- $isPoolMember := include "octo-mesh.isPoolMember" . }}
{{- if $isPoolMember }}
{{/*
  🔴 The ENV names must spell AdapterPoolTenantId / AdapterPoolRtId, the VALUES keys must not.
  They are two different contracts and they were renamed apart (AB#4924).

  The env names bind `AdapterPoolMemberOptions` in octo-communication-sdk: section "AdapterPool"
  plus properties `AdapterPoolTenantId` / `AdapterPoolRtId`, so the keys are
  OCTO_ADAPTERPOOL__ADAPTERPOOLTENANTID / __ADAPTERPOOLRTID. Stuttery, and not ours to shorten.
  Emitting the old POOLTENANTID / POOLRTID binds nothing, `IsEnabled` reads false, and the process
  starts as an ORDINARY ADAPTER with no tenant and no rtId — running, healthy, 1/1 Ready, and not a
  pool member. Observed on a local kind cluster.

  The values keys stay `adapterPool.poolTenantId` / `.poolRtId`: the `adapterPool.` prefix already
  says which pool, and the communication controller writes exactly those two paths
  (DeploymentSiteService.AppendAdapterPoolMemberOverrides). Renaming them here breaks that instead.
*/}}
- name: OCTO_ADAPTERPOOL__ADAPTERPOOLTENANTID
  value: {{ .Values.adapterPool.poolTenantId | quote }}
- name: OCTO_ADAPTERPOOL__ADAPTERPOOLRTID
  value: {{ .Values.adapterPool.poolRtId | quote }}
{{- else }}
{{/*
  AB#4924 increment 3 renamed this: the property behind OCTO_ADAPTER__TENANTID was
  DELETED, and the key only still works because ConfigureLegacyAdapterTenantId binds
  it for one deprecation release — at the price of a warning in this adapter's log on
  every start, naming this chart as the thing to fix. DEDICATEDTENANTID is the name
  that says what it is: the single tenant a dedicated adapter is pinned to, used for
  its hub route and its own credential and for nothing on the execution path.
*/}}
- name: OCTO_ADAPTER__DEDICATEDTENANTID
  value: {{ .Values.tenantId | quote }}
- name: OCTO_ADAPTER__ADAPTERRTID
  value: {{ .Values.adapterRtId | quote }}
{{- end }}
- name: OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI
  value: {{ .Values.communicationControllerServiceUri | quote }}
{{- if .Values.ignoreCertificateValidation }}
{{/*
  🔴 AB#5303 items 1+2. Rendered ONLY when set, and never `false`: an absent variable and a
  false one mean the same thing to the SDK, and the absent one cannot be mistaken for a
  deliberate choice in `kubectl describe pod`.

  Until AB#5303 this variable had no route into the container at all, and the one place that
  read it set ServicePointManager, which SocketsHttpHandler ignores. It now turns off TLS
  certificate validation for every outgoing Octo call of the process — and the SDK refuses it
  when ASPNETCORE_ENVIRONMENT says Production. Development and private-PKI clusters only; the
  supported answer for the latter is a trusted CA in the image (AB#5303 item 3), not this.
*/}}
- name: OCTO_ADAPTER__IGNORECERTIFICATEVALIDATION
  value: "true"
{{- end }}
- name: OCTO_ADAPTER__ADAPTERCKTYPEID
  value: "System.Communication/Adapter"
- name: OCTO_ADAPTER__REPORTINGSERVICEURL
  value: {{ .Values.reportingServiceUri | quote }}
{{- if .Values.authUri }}
- name: OCTO_ADAPTER__AUTHORITYURL
  value: {{ .Values.authUri | quote }}
{{/*
  AB#5072 — the adapter's OUTBOUND credential.

  `AUTHORITYURL` above is the INBOUND direction: the issuer that secured
  `FromHttpRequest@2` routes accept on tokens presented TO the adapter
  (MeshAdapterConfiguration.AuthorityUrl, adapter repo). `ISSUERURI` is the
  identity service the adapter authenticates ITSELF against before it connects
  to `/{tenantId}/adapterHub` (AdapterOptions.IssuerUri, octo-communication-sdk).

  Both are fed from the SAME `authUri` value on purpose — one identity service
  issues and validates both directions, so a second chart value could only ever
  drift. The two config keys exist because AdapterOptions lives in the SDK and
  must also serve adapters that have no MeshAdapterConfiguration (Loxone,
  Modbus, Zenon, the simulation plug); the chart is where they are tied back
  together. It must be the PUBLIC issuer address, not a cluster-internal
  service name: OIDC discovery runs against it and the communication controller
  validates the issuer of the resulting token.
*/}}
- name: OCTO_ADAPTER__ISSUERURI
  value: {{ .Values.authUri | quote }}
{{- end }}
{{/*
  AB#5232 — split-horizon issuer widening. Indexed env vars because
  MeshAdapterConfiguration.AdditionalValidIssuers is a string array
  (OCTO_ADAPTER__ADDITIONALVALIDISSUERS__0, __1, ...). Rendered independently
  of authUri: the entries only widen the issuer string comparison, and an
  adapter whose authority comes from its compiled-in default (local dev) must
  still be able to accept extra issuers.
*/}}
{{- range $i, $issuer := .Values.additionalValidIssuers }}
- name: OCTO_ADAPTER__ADDITIONALVALIDISSUERS__{{ $i }}
  value: {{ $issuer | quote }}
{{- end }}
{{/*
  Client id of the adapter's own confidential OAuth client — the
  `ServiceAccountConfiguration` the communication controller provisions per
  adapter (AB#5027) and projects onto this path as a `ValueOverride` at deploy
  time. Omitted when unset: `AdapterOptions.IsEnabled` is
  `IssuerUri && ClientId`, so an unconfigured adapter acquires no token and
  connects anonymously exactly as the whole fleet does today. Rendering it as
  an empty string would be the same thing, but the absent env var keeps the
  "nothing was configured here" state readable in a `kubectl describe pod`.
*/}}
{{- if .Values.serviceAccountClientId }}
- name: OCTO_ADAPTER__CLIENTID
  value: {{ .Values.serviceAccountClientId | quote }}
{{- end }}
{{/*
  🔴 Secret-flagged. The controller marks the matching `ValueOverride`
  `IsSecret=true`, so the operator materialises it into `{release}-octo-secrets`
  and hands this path a `{valueFrom: {secretKeyRef: ...}}` map instead of the
  plaintext — `octo-mesh.secretEnv` accepts both shapes, exactly as
  `secrets.rabbitmq` does. The value must never be rendered into the pod spec
  as a literal, and never into a values file that ends up in a helm release
  secret in cleartext.

  Guarded by `if` because `octo-mesh.secretEnv` FAILS on an empty value (that
  is deliberate for the mandatory cluster secrets) while this one is optional
  by design — see the `ClientId` note above.
*/}}
{{- if .Values.secrets.serviceAccountClientSecret }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_ADAPTER__CLIENTSECRET" "value" .Values.secrets.serviceAccountClientSecret "legacyKey" "serviceAccountClientSecret" "context" .) }}
{{- end }}
{{/*
  AB#5449 — IronOCR licence key for `PdfOcrExtraction@1`. Until now it sat in
  plain text in the node's source, so every rotation was a code change and every
  clone of the repository carried a live commercial key. It travels the same way
  the services' AutoMapper key does: configuration at deploy time, never
  compiled in.

  Secret-flagged, so `octo-mesh.secretEnv` is used rather than a literal env
  var — it accepts both the plaintext string and the
  `{valueFrom: {secretKeyRef: ...}}` map the operator materialises, exactly like
  `secrets.serviceAccountClientSecret` above.

  Guarded by `if` for two reasons: `octo-mesh.secretEnv` FAILS on an empty value,
  and this key is optional BY DESIGN. Only one node needs it and most tenants
  never run OCR, so an adapter without it must still start and serve everything
  else — `PdfOcrExtraction@1` fails on first use with a message naming this
  setting. A startup requirement here would take every adapter in the estate
  down the day the licence expires, including the ones that never OCR anything.
*/}}
{{- if .Values.secrets.ironOcrLicenseKey }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_ADAPTER__IRONOCRLICENSEKEY" "value" .Values.secrets.ironOcrLicenseKey "legacyKey" "ironOcrLicenseKey" "context" .) }}
{{- end }}
{{- end }}
