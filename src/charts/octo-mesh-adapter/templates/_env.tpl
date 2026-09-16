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
- name: OCTO_ADAPTERPOOL__POOLTENANTID
  value: {{ .Values.adapterPool.poolTenantId | quote }}
- name: OCTO_ADAPTERPOOL__POOLRTID
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
{{- end }}