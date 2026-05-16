
{{/*
Create a default fully qualified app name for adapters
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "octo-mesh.adapterFullname" -}}
    {{- if .Values.fullnameOverride }}
        {{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
    {{- else }}
        {{- $name := default "mesh-adapter" .Values.nameOverride  }}
        {{- printf "%s-%s-%s-%s" .Release.Name $name .Values.tenantId .Values.adapterRtId | lower | trunc 63 | trimSuffix "-" | lower }}
    {{- end }}
{{- end }}

{{/*
Expand the name of the chart.
*/}}
{{- define "octo-mesh.name" -}}
{{- default .Chart.Name | trunc 63 | trimSuffix "-" | lower }}
{{- end }}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "octo-mesh.fullname" -}}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{/*
Create a default fully qualified app name of a service
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "octo-mesh.service-fullname" -}}
    {{- if .Values.fullnameOverride }}
        {{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
    {{- else }}
        {{- $name := default "meshAdapter" .Values.nameOverride  }}
        {{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" | lower }}
    {{- end }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "octo-mesh.selectorLabels" -}}
app.kubernetes.io/name: {{ include "octo-mesh.name" . }}
app.kubernetes.io/instance: {{ include "octo-mesh.fullname" . }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "octo-mesh.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "octo-mesh.labels" -}}
helm.sh/chart: {{ include "octo-mesh.chart" . }}
{{ include "octo-mesh.selectorLabels" .  }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels service related
*/}}
{{- define "octo-mesh.service-selectorLabels" -}}
{{ include "octo-mesh.selectorLabels" . }}
app.kubernetes.io/service: {{ include "octo-mesh.service-fullname" . }}
{{- end }}

{{/*
Common labels service related
*/}}
{{- define "octo-mesh.service-labels" -}}
{{ include "octo-mesh.service-selectorLabels" . }}
{{- end }}

{{/*
Check if a mandadory value exists
*/}}
{{- define "checkMandatoryValue" -}}
{{- if not .value -}}
{{- fail (printf "Value %s does not exist. Please define a corresponding value." .name) -}}
{{- end -}}
{{- end -}}

{{/*
Render an env var entry whose value is a secret. The input value supports
two shapes:

  1. string (legacy): the chart packs it into the chart-owned `<fullname>-backend`
     Secret and renders a `valueFrom.secretKeyRef` pointing at that Secret with
     the legacyKey as the key name.
  2. map with `valueFrom`: rendered straight through, so the caller can point
     at an externally-managed Secret (operator-managed `<release>-octo-secrets`,
     External Secrets, etc.).

Anything else fails the template — silent acceptance would mask typos.

Usage:
  {{ include "octo-mesh.secretEnv" (dict
       "envName"   "OCTO_SYSTEM__DATABASEUSERPASSWORD"
       "value"     .Values.secrets.databaseUser
       "legacyKey" "databaseUser"
       "context"   .
  ) }}
*/}}
{{- define "octo-mesh.secretEnv" -}}
- name: {{ .envName }}
{{- if kindIs "map" .value }}
  {{- if hasKey .value "valueFrom" }}
  {{- toYaml .value | nindent 2 }}
  {{- else }}
  {{- fail (printf "secrets.%s must be a string or a {valueFrom: secretKeyRef: ...} map" .legacyKey) }}
  {{- end }}
{{- else if .value }}
  valueFrom:
    secretKeyRef:
      name: {{ printf "%s-backend" (include "octo-mesh.fullname" .context) }}
      key: {{ .legacyKey }}
{{- else }}
  {{- fail (printf "secrets.%s must be set (either a plaintext string or a valueFrom map)" .legacyKey) }}
{{- end }}
{{- end -}}

{{/*
Returns "true" when any of the secret fields used by the chart-owned
`<fullname>-backend` Secret is a plaintext string. Used by secret.yaml to
skip emitting the resource entirely when every value is sourced
externally via valueFrom.
*/}}
{{- define "octo-mesh.hasPlaintextBackendSecret" -}}
{{- $s := .Values.secrets -}}
{{- if and $s.databaseUser (not (kindIs "map" $s.databaseUser)) -}}true
{{- else if and $s.databaseAdmin (not (kindIs "map" $s.databaseAdmin)) -}}true
{{- else if and $s.rabbitmq (not (kindIs "map" $s.rabbitmq)) -}}true
{{- else if and $s.streamDataPassword (not (kindIs "map" $s.streamDataPassword)) -}}true
{{- end -}}
{{- end -}}
