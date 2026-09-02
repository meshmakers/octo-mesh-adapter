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