#!/usr/bin/env bash
# AB#4924 E3 — automated guard for what the octo-mesh-adapter chart hands an adapter POOL MEMBER.
#
# Decision E3 (2026-10-07): a pool member gets the IronOCR licence, and NEVER the installation's
# database/admin/CrateDB passwords or the SECRET attribute key ring. A member executes work for
# tenants other than the one that owns it; a standing installation-wide credential would make the
# per-lease credential decorative. The communication operator already withholds the cluster-secret
# tier from an AdapterPool (WorkloadReconciler.AppendClusterSecrets, pinned by the operator's
# AppendClusterSecretsTests); this script pins the chart's half, so neither layer relies on the other.
#
# Renders the chart with `helm template` (no cluster needed) and asserts on the WHOLE render — env
# vars AND the chart-owned Secret — for:
#   member   (operator shape: secrets as valueFrom maps)        IronOCR yes; DB passwords, key ring no
#   member   (hand-written shape: plaintext secrets)            same, and the sentinels nowhere, not
#                                                               even base64-encoded in the Secret
#   dedicated (positive control)                                DB passwords and key ring present
#
# Requirements: bash, helm >= 3, base64. Exit code 0 = all assertions hold.
# Usage:        tests/chart/test-chart.sh            (from anywhere)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
chart="$here/../../src/charts/octo-mesh-adapter"
failures=0
checks=0

render() { # $1 = release name, rest = helm args
  local release="$1"; shift
  helm template "$release" "$chart" "$@" 2>/dev/null
}

pass() { checks=$((checks + 1)); }
fail() { checks=$((checks + 1)); failures=$((failures + 1)); echo "  FAIL: $*" >&2; }

expect_contains() { # $1 = label, $2 = render, $3 = fixed string
  if grep -qF -- "$3" <<<"$2"; then pass; else fail "$1: expected '$3' in the render"; fi
}

expect_absent() { # $1 = label, $2 = render, $3 = fixed string
  if grep -qF -- "$3" <<<"$2"; then
    fail "$1: '$3' must not appear in the render:"
    grep -nF -- "$3" <<<"$2" | sed 's/^/        /' >&2
  else
    pass
  fi
}

b64() { printf '%s' "$1" | base64 | tr -d '\n'; }

forbidden_env=(
  OCTO_SYSTEM__DATABASEUSERPASSWORD
  OCTO_SYSTEM__ADMINUSERPASSWORD
  OCTO_ADAPTER__STREAMDATAPASSWORD
  OCTO_SECRETENCRYPTION__
  OCTO_ADAPTER__DEDICATEDTENANTID
)
sentinels=(
  SENTINEL-DATABASE-USER-PASSWORD
  SENTINEL-DATABASE-ADMIN-PASSWORD
  SENTINEL-STREAMDATA-PASSWORD
  SENTINEL-KEYRING-K1
  SENTINEL-KEYRING-LEGACY-V1
)
forbidden_secret_keys=(databaseUser: databaseAdmin: streamDataPassword: secretEncryptionKey- secretEncryptionLegacyV1Key:)

assert_member() { # $1 = label, $2 = render
  local label="$1" out="$2"
  [[ -n "$out" ]] || { fail "$label: helm template rendered nothing (or failed)"; return; }
  expect_contains "$label" "$out" "OCTO_ADAPTERPOOL__ADAPTERPOOLTENANTID"
  expect_contains "$label" "$out" "OCTO_ADAPTERPOOL__ADAPTERPOOLRTID"
  expect_contains "$label" "$out" "OCTO_ADAPTER__IRONOCRLICENSEKEY"
  for v in "${forbidden_env[@]}"; do expect_absent "$label" "$out" "$v"; done
  for v in "${forbidden_secret_keys[@]}"; do expect_absent "$label" "$out" "$v"; done
  for s in "${sentinels[@]}"; do
    expect_absent "$label" "$out" "$s"
    expect_absent "$label (base64)" "$out" "$(b64 "$s")"
  done
}

echo "octo-mesh-adapter chart: E3 pool member secrets"

# 1. Member, operator shape (valueFrom maps for broker/client/IronOCR, sentinels for the rest).
member_operator="$(render member -f "$here/member-values.yaml")"
assert_member "member (operator shape)" "$member_operator"

# 2. Member, hand-written shape: every secret plaintext, so the chart-owned Secret IS rendered.
member_plain="$(render member -f "$here/member-values.yaml" \
  --set secrets.rabbitmq=plain-broker-password \
  --set secrets.serviceAccountClientSecret=plain-client-secret \
  --set secrets.ironOcrLicenseKey=plain-ironocr-key)"
assert_member "member (plaintext shape)" "$member_plain"
expect_contains "member (plaintext shape)" "$member_plain" "kind: Secret"
expect_contains "member (plaintext shape)" "$member_plain" "ironOcrLicenseKey"
expect_contains "member (plaintext shape)" "$member_plain" "rabbitmq: $(printf '"%s"' "$(b64 plain-broker-password)")"

# 3. Dedicated adapter — positive control: everything the member is denied is rendered here.
dedicated="$(render dedicated -f "$here/dedicated-values.yaml")"
[[ -n "$dedicated" ]] || fail "dedicated: helm template rendered nothing (or failed)"
for v in OCTO_SYSTEM__DATABASEUSERPASSWORD OCTO_SYSTEM__ADMINUSERPASSWORD OCTO_ADAPTER__STREAMDATAPASSWORD \
         OCTO_SECRETENCRYPTION__KEYS__k1 OCTO_SECRETENCRYPTION__ACTIVEKEYID OCTO_SECRETENCRYPTION__LEGACYV1KEY \
         OCTO_ADAPTER__IRONOCRLICENSEKEY OCTO_ADAPTER__DEDICATEDTENANTID; do
  expect_contains "dedicated" "$dedicated" "$v"
done
expect_absent "dedicated" "$dedicated" "OCTO_ADAPTERPOOL__"

# 4. Dedicated, plaintext key ring: the chart-owned Secret carries it (the member must not).
dedicated_plain="$(render dedicated -f "$here/dedicated-values.yaml" \
  --set secrets.databaseUser=SENTINEL-DATABASE-USER-PASSWORD \
  --set secrets.secretEncryption.keys.k1=SENTINEL-KEYRING-K1)"
expect_contains "dedicated (plaintext shape)" "$dedicated_plain" "databaseUser: \"$(b64 SENTINEL-DATABASE-USER-PASSWORD)\""
expect_contains "dedicated (plaintext shape)" "$dedicated_plain" "secretEncryptionKey-k1: \"$(b64 SENTINEL-KEYRING-K1)\""

echo "  $((checks - failures))/$checks checks passed"
if ((failures > 0)); then
  exit 1
fi
