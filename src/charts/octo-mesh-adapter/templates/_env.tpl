{{- define "octo-mesh.system-env" -}}
- name: OCTO_SYSTEM__DATABASEHOST
  value: {{ .Values.clusterDependencies.mongodbHost }}
{{- if .Values.clusterDependencies.mongodbReplicaSet }}
- name: OCTO_SYSTEM__REPLICASETNAME
  value: {{ .Values.clusterDependencies.mongodbReplicaSet }}
{{- end }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__DATABASEUSERPASSWORD" "value" .Values.secrets.databaseUser "legacyKey" "databaseUser" "context" .) }}
{{ include "octo-mesh.secretEnv" (dict "envName" "OCTO_SYSTEM__ADMINUSERPASSWORD" "value" .Values.secrets.databaseAdmin "legacyKey" "databaseAdmin" "context" .) }}
{{- end }}

{{- define "octo-mesh.broker-env" -}}
- name: {{ printf "%s__BROKERHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqHost }}
- name: {{ printf "%s__BROKERUSERNAME" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.rabbitMqUser }}
{{ include "octo-mesh.secretEnv" (dict "envName" (printf "%s__BROKERPASSWORD" (upper .name)) "value" .global.Values.secrets.rabbitmq "legacyKey" "rabbitmq" "context" .global) }}
{{- end }}

{{- define "octo-mesh.streamdata-env" -}}
# Instance-level kill switch for StreamData. Read by
# StreamDataInstanceConfiguration (root "StreamData" config section, hence
# the fixed env-var name without a service prefix). Defaults to false so
# the feature is opt-in per cluster.
- name: OCTO_STREAMDATA__ENABLED
  value: {{ .global.Values.clusterDependencies.streamDataEnabled | quote }}
- name: {{ printf "%s__STREAMDATAHOST" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataHost }}
- name: {{ printf "%s__STREAMDATAUSER" (upper .name) }}
  value: {{ .global.Values.clusterDependencies.streamDataUser }}
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
  value: {{ .Values.instancePrefix }}
- name: OCTO_ADAPTER__TENANTID
  value: {{ .Values.tenantId }}
- name: OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI
  value: {{ .Values.communicationControllerServiceUri }}
- name: OCTO_ADAPTER__ADAPTERCKTYPEID
  value: "System.Communication/Adapter"              
- name: OCTO_ADAPTER__ADAPTERRTID
  value: {{ .Values.adapterRtId }}
- name: OCTO_ADAPTER__REPORTINGSERVICEURL
  value: {{ .Values.reportingServiceUri }}
{{- end }}