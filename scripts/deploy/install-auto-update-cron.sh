#!/usr/bin/env bash

set -euo pipefail

# Installs a root cron job that periodically runs auto-update-linux.sh.
#
# Usage:
#   sudo ./scripts/deploy/install-auto-update-cron.sh
#
# Optional environment overrides:
#   APP_NAME=energyflowpilot
#   SERVICE_NAME=energyflowpilot-api.service
#   PUBLISH_RUNTIME=linux-arm64
#   BACKEND_PORT=5094
#   GIT_BRANCH=develop
#   CRON_SCHEDULE="*/5 * * * *"

APP_NAME="${APP_NAME:-energyflowpilot}"
SERVICE_NAME="${SERVICE_NAME:-${APP_NAME}-api.service}"
PUBLISH_RUNTIME="${PUBLISH_RUNTIME:-linux-arm64}"
BACKEND_PORT="${BACKEND_PORT:-5094}"
GIT_BRANCH="${GIT_BRANCH:-develop}"
CRON_SCHEDULE="${CRON_SCHEDULE:-*/5 * * * *}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
AUTO_UPDATE_SCRIPT="${SCRIPT_DIR}/auto-update-linux.sh"
CRON_FILE="/etc/cron.d/${APP_NAME}-auto-update"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Dieses Skript muss mit Root-Rechten ausgefuehrt werden." >&2
    exit 1
  fi
}

validate_environment() {
  if [[ ! -f "${AUTO_UPDATE_SCRIPT}" ]]; then
    echo "Auto-Update-Skript nicht gefunden: ${AUTO_UPDATE_SCRIPT}" >&2
    exit 1
  fi
}

write_cron_file() {
  cat > "${CRON_FILE}" <<EOF
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

${CRON_SCHEDULE} root APP_NAME=${APP_NAME} SERVICE_NAME=${SERVICE_NAME} PUBLISH_RUNTIME=${PUBLISH_RUNTIME} BACKEND_PORT=${BACKEND_PORT} GIT_BRANCH=${GIT_BRANCH} ${AUTO_UPDATE_SCRIPT}
EOF

  chmod 0644 "${CRON_FILE}"
}

print_summary() {
  cat <<EOF

Auto-Update-Cronjob installiert:

- Datei: ${CRON_FILE}
- Zeitplan: ${CRON_SCHEDULE}
- Branch: ${GIT_BRANCH}
- Script: ${AUTO_UPDATE_SCRIPT}
- Log: /var/log/${APP_NAME}/auto-update.log

Pruefen:
- cat ${CRON_FILE}
- tail -n 100 /var/log/${APP_NAME}/auto-update.log

EOF
}

main() {
  require_root
  validate_environment
  write_cron_file
  print_summary
}

main "$@"
