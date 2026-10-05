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
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__DATABASEUSERPASSWORD" "value" .Values.secrets.databaseUser "legacyKey" "databaseUser" "context" .) }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__ADMINUSERPASSWORD" "value" .Values.secrets.databaseAdmin "legacyKey" "databaseAdmin" "context" .) }}
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
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__STREAMDATAPASSWORD" (upper .name)) "value" .global.Values.secrets.streamDataPassword "legacyKey" "streamDataPassword" "context" .global) }}
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
- name: OCTO_ADAPTER__TENANTID
  value: {{ .Values.tenantId | quote }}
- name: OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI
  value: {{ .Values.communicationControllerServiceUri | quote }}
- name: OCTO_ADAPTER__ADAPTERCKTYPEID
  value: "System.Communication/Adapter"
- name: OCTO_ADAPTER__ADAPTERRTID
  value: {{ .Values.adapterRtId | quote }}
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
