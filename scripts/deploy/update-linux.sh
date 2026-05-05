#!/usr/bin/env bash

set -euo pipefail

# Updates an existing EnergyFlowPilot Linux installation from the current Git checkout.
# This script assumes scripts/deploy/setup-raspberry.sh has already prepared:
# - systemd service
# - nginx static frontend root
# - persistent config/data/log directories
#
# Usage:
#   sudo ./scripts/deploy/update-linux.sh
#
# Optional environment overrides:
#   APP_NAME=energyflowpilot
#   SERVICE_NAME=energyflowpilot-api.service
#   PUBLISH_RUNTIME=linux-arm64
#   BACKEND_PORT=5094
#   GIT_BRANCH=develop

APP_NAME="${APP_NAME:-energyflowpilot}"
SERVICE_USER="${APP_NAME}"
SERVICE_GROUP="${APP_NAME}"
SERVICE_NAME="${SERVICE_NAME:-${APP_NAME}-api.service}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

API_PROJECT_PATH="${REPO_ROOT}/src/TibberVictronController.Api/TibberVictronController.Api.csproj"
FRONTEND_PATH="${REPO_ROOT}/src/TibberVictronController.Frontend"

INSTALL_ROOT="/opt/${APP_NAME}"
API_PUBLISH_DIR="${INSTALL_ROOT}/api"
API_STAGING_DIR="${INSTALL_ROOT}/api-next"
FRONTEND_WEB_ROOT="/var/www/${APP_NAME}"

BACKEND_PORT="${BACKEND_PORT:-5094}"
PUBLISH_RUNTIME="${PUBLISH_RUNTIME:-linux-arm64}"
HEALTH_CHECK_URL="http://127.0.0.1:${BACKEND_PORT}/health"
GIT_BRANCH="${GIT_BRANCH:-}"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Dieses Update-Skript muss mit Root-Rechten ausgefuehrt werden." >&2
    exit 1
  fi
}

require_command() {
  local command_name="$1"

  if ! command -v "${command_name}" >/dev/null 2>&1; then
    echo "Benoetigter Befehl fehlt: ${command_name}" >&2
    exit 1
  fi
}

print_matching_services() {
  local matches
  matches="$(systemctl list-unit-files --type=service --no-legend 2>/dev/null \
    | awk '{ print $1 }' \
    | grep -Ei 'energy|flow|pilot|tibber|victron' || true)"

  if [[ -n "${matches}" ]]; then
    echo "Gefundene aehnliche Services:" >&2
    echo "${matches}" >&2
  fi
}

validate_environment() {
  require_command git
  require_command dotnet
  require_command npm
  require_command rsync
  require_command systemctl
  require_command curl

  if [[ ! -d "${REPO_ROOT}/.git" ]]; then
    echo "Kein Git-Repository gefunden: ${REPO_ROOT}" >&2
    exit 1
  fi

  if [[ ! -f "${API_PROJECT_PATH}" ]]; then
    echo "Backend-Projekt nicht gefunden: ${API_PROJECT_PATH}" >&2
    exit 1
  fi

  if [[ ! -f "${FRONTEND_PATH}/package.json" ]]; then
    echo "Frontend-Projekt nicht gefunden: ${FRONTEND_PATH}/package.json" >&2
    exit 1
  fi

  if [[ ! -d "${API_PUBLISH_DIR}" ]]; then
    echo "Backend-Publish-Verzeichnis fehlt: ${API_PUBLISH_DIR}" >&2
    echo "Bitte zuerst setup-raspberry.sh ausfuehren." >&2
    exit 1
  fi

  if [[ ! -d "${FRONTEND_WEB_ROOT}" ]]; then
    echo "Frontend-Webroot fehlt: ${FRONTEND_WEB_ROOT}" >&2
    echo "Bitte zuerst setup-raspberry.sh ausfuehren." >&2
    exit 1
  fi

  if ! systemctl list-unit-files | grep -q "^${SERVICE_NAME}"; then
    echo "systemd-Service fehlt: ${SERVICE_NAME}" >&2
    print_matching_services
    echo "Falls der Service anders heisst, starte mit SERVICE_NAME=<name>.service." >&2
    echo "Falls APP_NAME abweicht, starte mit APP_NAME=<name>." >&2
    echo "Wenn noch kein Service existiert, bitte zuerst setup-raspberry.sh ausfuehren." >&2
    exit 1
  fi
}

validate_clean_worktree() {
  if [[ -n "$(git -C "${REPO_ROOT}" status --porcelain)" ]]; then
    echo "Das Repository hat lokale Aenderungen. Update wird abgebrochen." >&2
    echo "Bitte committen, stashen oder bewusst bereinigen:" >&2
    git -C "${REPO_ROOT}" status --short >&2
    exit 1
  fi
}

update_repository() {
  git config --global --add safe.directory "${REPO_ROOT}" >/dev/null 2>&1 || true

  if [[ -n "${GIT_BRANCH}" ]]; then
    git -C "${REPO_ROOT}" checkout "${GIT_BRANCH}"
  fi

  validate_clean_worktree
  git -C "${REPO_ROOT}" fetch --prune
  git -C "${REPO_ROOT}" pull --ff-only
}

publish_backend_to_staging() {
  rm -rf "${API_STAGING_DIR}"
  install -d -m 0755 "${API_STAGING_DIR}"

  dotnet restore "${API_PROJECT_PATH}"
  dotnet publish "${API_PROJECT_PATH}" \
    --configuration Release \
    --runtime "${PUBLISH_RUNTIME}" \
    --self-contained false \
    --output "${API_STAGING_DIR}"

  chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${API_STAGING_DIR}"
}

build_frontend() {
  pushd "${FRONTEND_PATH}" >/dev/null

  if [[ -f package-lock.json ]]; then
    npm ci
  else
    npm install
  fi

  npm run build
  popd >/dev/null
}

deploy_files() {
  local service_was_active=0
  local deploy_status=0

  if systemctl is-active --quiet "${SERVICE_NAME}"; then
    service_was_active=1
  fi

  systemctl stop "${SERVICE_NAME}"

  set +e
  rsync -a --delete "${API_STAGING_DIR}/" "${API_PUBLISH_DIR}/" &&
    chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${API_PUBLISH_DIR}" &&
    rsync -a --delete "${FRONTEND_PATH}/dist/" "${FRONTEND_WEB_ROOT}/" &&
    chown -R www-data:www-data "${FRONTEND_WEB_ROOT}"
  deploy_status="$?"
  set -e

  if [[ "${deploy_status}" -ne 0 ]]; then
    echo "Dateiaustausch fehlgeschlagen." >&2

    if [[ "${service_was_active}" -eq 1 ]]; then
      systemctl start "${SERVICE_NAME}" || true
    fi

    return "${deploy_status}"
  fi

  systemctl start "${SERVICE_NAME}"

  if systemctl list-unit-files | grep -q "^nginx.service"; then
    systemctl reload nginx || systemctl restart nginx
  fi
}

wait_for_healthcheck() {
  local attempts=30
  local delay_seconds=2
  local attempt

  for (( attempt=1; attempt<=attempts; attempt++ )); do
    if curl -fsS "${HEALTH_CHECK_URL}" >/dev/null 2>&1; then
      echo "Health-Check erfolgreich: ${HEALTH_CHECK_URL}"
      return 0
    fi

    sleep "${delay_seconds}"
  done

  echo "Warnung: Health-Check unter ${HEALTH_CHECK_URL} wurde nicht rechtzeitig erfolgreich." >&2
  echo "Bitte pruefen: systemctl status ${SERVICE_NAME} und journalctl -u ${SERVICE_NAME} -n 200 --no-pager" >&2
  return 1
}

cleanup() {
  rm -rf "${API_STAGING_DIR}"
}

print_summary() {
  local head_revision
  head_revision="$(git -C "${REPO_ROOT}" rev-parse --short HEAD)"

  cat <<EOF

EnergyFlowPilot Update abgeschlossen.

- Git-Revision: ${head_revision}
- Backend: ${API_PUBLISH_DIR}
- Frontend: ${FRONTEND_WEB_ROOT}
- Service: ${SERVICE_NAME}
- Health: ${HEALTH_CHECK_URL}

EOF
}

main() {
  require_root
  validate_environment
  update_repository
  publish_backend_to_staging
  build_frontend
  deploy_files
  wait_for_healthcheck || true
  cleanup
  print_summary
}

main "$@"
