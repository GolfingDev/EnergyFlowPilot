#!/usr/bin/env bash

set -euo pipefail

# This script prepares a Raspberry Pi (Debian 12 / Raspberry Pi OS Bookworm 64-bit)
# to host EnergyFlowPilot with:
# - ASP.NET Core backend as a systemd service
# - Vue frontend as static files behind nginx
# - SQLite database and log directories in persistent Linux paths

APP_NAME="energyflowpilot"
SERVICE_USER="${APP_NAME}"
SERVICE_GROUP="${APP_NAME}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

API_PROJECT_PATH="${REPO_ROOT}/src/TibberVictronController.Api/TibberVictronController.Api.csproj"
FRONTEND_PATH="${REPO_ROOT}/src/TibberVictronController.Frontend"

INSTALL_ROOT="/opt/${APP_NAME}"
API_PUBLISH_DIR="${INSTALL_ROOT}/api"
FRONTEND_WEB_ROOT="/var/www/${APP_NAME}"
CONFIG_ROOT="/etc/${APP_NAME}"
DATA_ROOT="/var/lib/${APP_NAME}"
LOG_ROOT="/var/log/${APP_NAME}"

SYSTEMD_SERVICE_FILE="/etc/systemd/system/${APP_NAME}-api.service"
NGINX_SITE_FILE="/etc/nginx/sites-available/${APP_NAME}.conf"
NGINX_SITE_LINK="/etc/nginx/sites-enabled/${APP_NAME}.conf"
LOGROTATE_FILE="/etc/logrotate.d/${APP_NAME}"
ENV_FILE="${CONFIG_ROOT}/api.env"

BACKEND_PORT="${BACKEND_PORT:-5094}"
NODE_MAJOR="${NODE_MAJOR:-22}"
DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
PUBLISH_RUNTIME="${PUBLISH_RUNTIME:-linux-arm64}"
HEALTH_CHECK_URL="http://127.0.0.1:${BACKEND_PORT}/health"
DEBIAN_VERSION_ID=""

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Dieses Skript muss mit Root-Rechten ausgeführt werden." >&2
    exit 1
  fi
}

validate_platform() {
  local architecture
  architecture="$(dpkg --print-architecture)"

  if [[ "${architecture}" != "arm64" ]]; then
    echo "Dieses Skript unterstützt aktuell nur arm64. Gefunden: ${architecture}" >&2
    echo ".NET 10 APT-Pakete sind für Raspberry Pi nur stabil auf arm64 sinnvoll." >&2
    exit 1
  fi

  if [[ ! -f /etc/os-release ]]; then
    echo "/etc/os-release fehlt. Die Distribution konnte nicht erkannt werden." >&2
    exit 1
  fi

  # shellcheck disable=SC1091
  source /etc/os-release

  DEBIAN_VERSION_ID="${VERSION_ID:-}"

  if [[ "${VERSION_CODENAME:-}" != "bookworm" && "${VERSION_CODENAME:-}" != "trixie" ]]; then
    echo "Dieses Skript ist für Debian 12 / Bookworm oder Debian 13 / Trixie ausgelegt. Gefunden: ${VERSION_CODENAME:-unbekannt}" >&2
    exit 1
  fi

  if [[ "${DEBIAN_VERSION_ID}" != "12" && "${DEBIAN_VERSION_ID}" != "13" ]]; then
    echo "Dieses Skript erwartet Debian-Version 12 oder 13. Gefunden: ${DEBIAN_VERSION_ID:-unbekannt}" >&2
    exit 1
  fi
}

validate_repo() {
  if [[ ! -f "${API_PROJECT_PATH}" ]]; then
    echo "Backend-Projekt nicht gefunden: ${API_PROJECT_PATH}" >&2
    exit 1
  fi

  if [[ ! -f "${FRONTEND_PATH}/package.json" ]]; then
    echo "Frontend-Projekt nicht gefunden: ${FRONTEND_PATH}/package.json" >&2
    exit 1
  fi
}

install_base_packages() {
  apt-get update
  apt-get install -y \
    apt-transport-https \
    ca-certificates \
    curl \
    git \
    gnupg \
    jq \
    lsb-release \
    nginx \
    rsync \
    sqlite3 \
    unzip \
    wget
}

install_dotnet_sdk() {
  local package_file="/tmp/packages-microsoft-prod.deb"

  if ! dpkg -s dotnet-sdk-10.0 >/dev/null 2>&1; then
    wget "https://packages.microsoft.com/config/debian/${DEBIAN_VERSION_ID}/packages-microsoft-prod.deb" -O "${package_file}"
    dpkg -i "${package_file}"
    rm -f "${package_file}"

    apt-get update
    apt-get install -y "dotnet-sdk-${DOTNET_CHANNEL}"
  fi
}

install_nodejs() {
  local installed_major=""

  if command -v node >/dev/null 2>&1; then
    installed_major="$(node -p "process.versions.node.split('.')[0]")"
  fi

  if [[ -z "${installed_major}" || "${installed_major}" -lt 20 ]]; then
    curl -fsSL "https://deb.nodesource.com/setup_${NODE_MAJOR}.x" | bash -
    apt-get install -y nodejs
  fi
}

create_service_user() {
  if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd \
      --system \
      --user-group \
      --home-dir "${INSTALL_ROOT}" \
      --shell /usr/sbin/nologin \
      "${SERVICE_USER}"
  fi
}

create_directories() {
  install -d -m 0755 "${INSTALL_ROOT}"
  install -d -m 0755 "${API_PUBLISH_DIR}"
  install -d -m 0755 "${FRONTEND_WEB_ROOT}"
  install -d -m 0755 "${CONFIG_ROOT}"
  install -d -m 0755 "${DATA_ROOT}"
  install -d -m 0755 "${DATA_ROOT}/.dotnet"
  install -d -m 0755 "${DATA_ROOT}/.net"
  install -d -m 0755 "${LOG_ROOT}"
  install -d -m 0755 "${LOG_ROOT}/api-errors"

  chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${INSTALL_ROOT}" "${DATA_ROOT}" "${LOG_ROOT}"
  chown -R www-data:www-data "${FRONTEND_WEB_ROOT}"
}

stop_existing_service() {
  if systemctl list-unit-files | grep -q "^${APP_NAME}-api.service"; then
    systemctl stop "${APP_NAME}-api.service" || true
  fi
}

publish_backend() {
  dotnet restore "${API_PROJECT_PATH}"
  dotnet publish "${API_PROJECT_PATH}" \
    --configuration Release \
    --runtime "${PUBLISH_RUNTIME}" \
    --self-contained false \
    --output "${API_PUBLISH_DIR}"

  chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${API_PUBLISH_DIR}"
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

  rm -rf "${FRONTEND_WEB_ROOT:?}/"*
  cp -R "${FRONTEND_PATH}/dist/." "${FRONTEND_WEB_ROOT}/"
  chown -R www-data:www-data "${FRONTEND_WEB_ROOT}"
}

write_environment_file() {
  if [[ ! -f "${ENV_FILE}" ]]; then
    cat > "${ENV_FILE}" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:${BACKEND_PORT}
ConnectionStrings__ControllerDatabase=Data Source=${DATA_ROOT}/tibber-victron-controller.db
FileExceptionLogging__LogDirectory=${LOG_ROOT}/api-errors
FileExceptionLogging__RetentionDays=14
DOTNET_PRINT_TELEMETRY_MESSAGE=false
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
DOTNET_BUNDLE_EXTRACT_BASE_DIR=${DATA_ROOT}/.net
EOF

    chmod 0640 "${ENV_FILE}"
    chown root:"${SERVICE_GROUP}" "${ENV_FILE}"
  fi
}

write_systemd_service() {
  cat > "${SYSTEMD_SERVICE_FILE}" <<EOF
[Unit]
Description=EnergyFlowPilot ASP.NET Core API
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${API_PUBLISH_DIR}
EnvironmentFile=${ENV_FILE}
Environment=HOME=${INSTALL_ROOT}
Environment=DOTNET_CLI_HOME=${DATA_ROOT}/.dotnet
ExecStart=/usr/bin/dotnet ${API_PUBLISH_DIR}/TibberVictronController.Api.dll
Restart=always
RestartSec=10
SyslogIdentifier=${APP_NAME}-api

[Install]
WantedBy=multi-user.target
EOF
}

write_nginx_config() {
  cat > "${NGINX_SITE_FILE}" <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name _;

    root ${FRONTEND_WEB_ROOT};
    index index.html;

    location /api/ {
        proxy_pass http://127.0.0.1:${BACKEND_PORT};
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
    }

    location = /health {
        proxy_pass http://127.0.0.1:${BACKEND_PORT};
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    location /hubs/ {
        proxy_pass http://127.0.0.1:${BACKEND_PORT};
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
EOF

  rm -f /etc/nginx/sites-enabled/default
  ln -sf "${NGINX_SITE_FILE}" "${NGINX_SITE_LINK}"
}

write_logrotate_config() {
  cat > "${LOGROTATE_FILE}" <<EOF
${LOG_ROOT}/api-errors/*.log {
    daily
    rotate 14
    missingok
    notifempty
    compress
    delaycompress
    copytruncate
    create 0640 ${SERVICE_USER} ${SERVICE_GROUP}
}
EOF
}

enable_services() {
  nginx -t
  systemctl daemon-reload
  systemctl enable --now "${APP_NAME}-api.service"
  systemctl enable --now nginx
  systemctl restart nginx
}

wait_for_healthcheck() {
  local attempts=30
  local delay_seconds=2
  local attempt

  for (( attempt=1; attempt<=attempts; attempt++ )); do
    if curl -fsS "${HEALTH_CHECK_URL}" >/dev/null 2>&1; then
      return 0
    fi

    sleep "${delay_seconds}"
  done

  echo "Warnung: Health-Check unter ${HEALTH_CHECK_URL} wurde nicht rechtzeitig erfolgreich." >&2
  echo "Bitte prüfen: systemctl status ${APP_NAME}-api und journalctl -u ${APP_NAME}-api -n 200" >&2
}

print_summary() {
  cat <<EOF

EnergyFlowPilot wurde vorbereitet.

Wichtige Pfade:
- Backend Publish: ${API_PUBLISH_DIR}
- Frontend Webroot: ${FRONTEND_WEB_ROOT}
- Konfiguration: ${ENV_FILE}
- Datenbank: ${DATA_ROOT}/tibber-victron-controller.db
- API-Fehlerlogs: ${LOG_ROOT}/api-errors
- Health-Check: ${HEALTH_CHECK_URL}

Wichtige Dienste:
- systemctl status ${APP_NAME}-api
- systemctl status nginx
- journalctl -u ${APP_NAME}-api -n 200 --no-pager

Wenn du produktive Werte setzen willst:
- ${ENV_FILE}
- ${API_PUBLISH_DIR}/appsettings.json oder Environment-Variablen

EOF
}

main() {
  require_root
  validate_platform
  validate_repo
  install_base_packages
  install_dotnet_sdk
  install_nodejs
  create_service_user
  create_directories
  stop_existing_service
  publish_backend
  build_frontend
  write_environment_file
  write_systemd_service
  write_nginx_config
  write_logrotate_config
  enable_services
  wait_for_healthcheck
  print_summary
}

main "$@"
