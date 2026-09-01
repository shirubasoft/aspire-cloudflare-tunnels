#!/usr/bin/env bash

set -euo pipefail

readonly REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly APPHOST="${ASPIRE_APPHOST:-sample/Shirubasoft.Aspire.CloudflareTunnels.Sample.AppHost.csproj}"
readonly DEPLOYMENT_ENVIRONMENT="${ASPIRE_TEST_ENVIRONMENT:-CloudflareIntegration}"
readonly PUBLIC_URL="${ASPIRE_TEST_URL:-https://autocreated.shiruba.dev}"

cd "$REPOSITORY_ROOT"

require_secret() {
  local name="$1"

  if [[ -z "${!name:-}" ]]; then
    printf '::error::%s repository secret is required.\n' "$name" >&2
    return 1
  fi
}

aspire_with_run_parameters() {
  env \
    "Parameters__my_cloudflare_tunnel_account_id=$CLOUDFLARE_ACCOUNT_ID" \
    "Parameters__my_cloudflare_tunnel_api_token=$CLOUDFLARE_API_TOKEN" \
    aspire "$@"
}

aspire_with_deployment_parameters() {
  env \
    "Parameters__my_cloudflare_tunnel_account_id=$CLOUDFLARE_ACCOUNT_ID" \
    "Parameters__my_cloudflare_tunnel_api_token=$CLOUDFLARE_API_TOKEN" \
    "Parameters__my_cloudflare_tunnel_tunnel_token=$CLOUDFLARE_TUNNEL_TOKEN" \
    aspire "$@"
}

show_container_diagnostics() {
  local runtime
  local connector

  for runtime in docker podman; do
    if ! command -v "$runtime" >/dev/null 2>&1; then
      continue
    fi

    "$runtime" ps -a || true
    connector="$("$runtime" ps -a --format '{{.Names}}' |
      grep 'my-cloudflare-.*tunnel' |
      head -n 1 || true)"

    if [[ -n "$connector" ]]; then
      "$runtime" logs --tail 100 "$connector" || true
    fi
  done
}

assert_public_endpoint() {
  local public_url="$1"
  local output_file="$2"

  curl \
    --fail \
    --silent \
    --show-error \
    --retry 12 \
    --retry-all-errors \
    --retry-delay 5 \
    --max-time 30 \
    --output "$output_file" \
    "$public_url"

  grep --quiet 'Welcome to nginx!' "$output_file"
}

wait_for_resource_url() {
  local resource_name="$1"
  local output_file="$2"
  local url_name="${3:-}"
  local expected_url="${4:-}"
  local deadline=$((SECONDS + 60))
  local resource_url

  while ((SECONDS < deadline)); do
    if aspire describe \
      --apphost "$APPHOST" \
      --format Json \
      --non-interactive > "$output_file" &&
      resource_url="$(jq --exit-status --raw-output \
        --arg resource_name "$resource_name" \
        --arg url_name "$url_name" \
        --arg expected_url "$expected_url" '
          .resources[] |
          select(.displayName == $resource_name) |
          .urls[] |
          select(
            ($url_name == "" or .name == $url_name) and
            ($expected_url == "" or .url == $expected_url)
          ) |
          .url
        ' "$output_file")"; then
      printf '%s\n' "$resource_url"
      return 0
    fi

    sleep 2
  done

  printf '::error::Timed out waiting for a dashboard URL on resource %s.\n' \
    "$resource_name" >&2
  return 1
}

run_deployment_pipeline_test() (
  cleanup() {
    local status=$?
    trap - EXIT

    if [[ $status -ne 0 ]]; then
      show_container_diagnostics
    fi

    aspire_with_deployment_parameters destroy \
      --apphost "$APPHOST" \
      --environment "$DEPLOYMENT_ENVIRONMENT" \
      --yes \
      --non-interactive || true

    exit "$status"
  }

  trap cleanup EXIT

  aspire_with_deployment_parameters deploy \
    --apphost "$APPHOST" \
    --environment "$DEPLOYMENT_ENVIRONMENT" \
    --non-interactive

  assert_public_endpoint \
    "$PUBLIC_URL" \
    "$TEST_OUTPUT_DIRECTORY/cloudflare-pipeline-response.html"
)

run_quick_tunnel_test() (
  cleanup() {
    local status=$?
    trap - EXIT

    if [[ $status -ne 0 ]]; then
      aspire describe --apphost "$APPHOST" --format Table --non-interactive || true
      aspire logs my-cloudflare-quick-tunnel --apphost "$APPHOST" --tail 100 --non-interactive || true
      show_container_diagnostics
    fi

    aspire stop --apphost "$APPHOST" --non-interactive || true
    exit "$status"
  }

  trap cleanup EXIT

  aspire start \
    --apphost "$APPHOST" \
    --non-interactive \
    --format Json \
    -- \
    --quick-tunnel > "$TEST_OUTPUT_DIRECTORY/aspire-quick-tunnel-start.json"

  aspire wait hello-world \
    --apphost "$APPHOST" \
    --status healthy \
    --timeout 180 \
    --non-interactive

  aspire wait my-cloudflare-quick-tunnel \
    --apphost "$APPHOST" \
    --status healthy \
    --timeout 180 \
    --non-interactive

  local quick_tunnel_url
  quick_tunnel_url="$(wait_for_resource_url \
    my-cloudflare-quick-tunnel \
    "$TEST_OUTPUT_DIRECTORY/aspire-quick-tunnel-describe.json" \
    public)"

  assert_public_endpoint \
    "$quick_tunnel_url" \
    "$TEST_OUTPUT_DIRECTORY/cloudflare-quick-tunnel-response.html"
)

run_local_apphost_test() (
  cleanup() {
    local status=$?
    trap - EXIT

    if [[ $status -ne 0 ]]; then
      aspire describe --apphost "$APPHOST" --format Table --non-interactive || true
      aspire logs my-cloudflare-tunnel-installer --apphost "$APPHOST" --tail 100 --non-interactive || true
      aspire logs my-cloudflare-tunnel-route-autocreated-shiruba-dev --apphost "$APPHOST" --tail 100 --non-interactive || true
      aspire logs my-cloudflare-tunnel --apphost "$APPHOST" --tail 100 --non-interactive || true
    fi

    aspire stop --apphost "$APPHOST" --non-interactive || true
    exit "$status"
  }

  trap cleanup EXIT

  aspire_with_run_parameters start \
    --apphost "$APPHOST" \
    --non-interactive \
    --format Json > "$TEST_OUTPUT_DIRECTORY/aspire-start.json"

  aspire wait hello-world \
    --apphost "$APPHOST" \
    --status healthy \
    --timeout 180 \
    --non-interactive

  aspire wait my-cloudflare-tunnel \
    --apphost "$APPHOST" \
    --status healthy \
    --timeout 180 \
    --non-interactive

  aspire wait my-cloudflare-tunnel-route-autocreated-shiruba-dev \
    --apphost "$APPHOST" \
    --status healthy \
    --timeout 180 \
    --non-interactive

  wait_for_resource_url \
    my-cloudflare-tunnel \
    "$TEST_OUTPUT_DIRECTORY/aspire-named-tunnel-describe.json" \
    "" \
    "$PUBLIC_URL" >/dev/null

  assert_public_endpoint \
    "$PUBLIC_URL" \
    "$TEST_OUTPUT_DIRECTORY/cloudflare-response.html"
)

if [[ -n "${RUNNER_TEMP:-}" ]]; then
  TEST_OUTPUT_DIRECTORY="$RUNNER_TEMP"
  readonly TEST_OUTPUT_DIRECTORY
else
  TEST_OUTPUT_DIRECTORY="$(mktemp -d)"
  readonly TEST_OUTPUT_DIRECTORY
  trap 'rm -rf -- "$TEST_OUTPUT_DIRECTORY"' EXIT
fi

run_quick_tunnel_test

require_secret CLOUDFLARE_ACCOUNT_ID
require_secret CLOUDFLARE_API_TOKEN
require_secret CLOUDFLARE_TUNNEL_TOKEN

run_deployment_pipeline_test
run_local_apphost_test
