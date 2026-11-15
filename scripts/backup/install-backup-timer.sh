#!/usr/bin/env bash

set -euo pipefail

# Installs the nightly backup: a settings file, a passphrase (created once) and a systemd timer that runs
# backup-linux.sh. It does not sign in to the cloud storage; that is done once by hand with rclone
# (see docs/30-operations/backup-and-restore.md). Running it again changes nothing that already exists.
#
# Usage:
#   sudo ./scripts/backup/install-backup-timer.sh
#
# Optional environment overrides:
#   APP_NAME=energyflowpilot
#   BACKUP_SCHEDULE="*-*-* 03:20:00"

APP_NAME="${APP_NAME:-energyflowpilot}"
BACKUP_SCHEDULE="${BACKUP_SCHEDULE:-*-*-* 03:20:00}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKUP_SCRIPT="${SCRIPT_DIR}/backup-linux.sh"
CONFIG_DIR="/etc/${APP_NAME}"
CONFIG_FILE="${CONFIG_DIR}/backup.env"
PASSPHRASE_FILE="${CONFIG_DIR}/backup.passphrase"
SERVICE_FILE="/etc/systemd/system/${APP_NAME}-backup.service"
TIMER_FILE="/etc/systemd/system/${APP_NAME}-backup.timer"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Dieses Skript muss mit Root-Rechten ausgefuehrt werden." >&2
    exit 1
  fi
}

validate_environment() {
  if [[ ! -f "${BACKUP_SCRIPT}" ]]; then
    echo "Sicherungsskript nicht gefunden: ${BACKUP_SCRIPT}" >&2
    exit 1
  fi
  local tool
  for tool in sqlite3 gpg openssl; do
    if ! command -v "${tool}" >/dev/null 2>&1; then
      echo "Benoetigtes Programm fehlt: ${tool}" >&2
      exit 1
    fi
  done
  if ! command -v rclone >/dev/null 2>&1; then
    echo "Hinweis: rclone ist nicht installiert (sudo apt-get install rclone). Ohne rclone bleibt die Sicherung auf dem Pi."
  fi
}

write_config_file() {
  mkdir -p "${CONFIG_DIR}"
  if [[ -f "${CONFIG_FILE}" ]]; then
    return
  fi
  cat > "${CONFIG_FILE}" <<EOF
# Settings of the nightly backup (scripts/backup/backup-linux.sh).
# Target in rclone notation, for example: onedrive:EnergyFlowPilot-Backup
# Empty: the archive stays on the Pi only and does not protect against a dead SD card.
RCLONE_REMOTE=
RCLONE_CONFIG=${CONFIG_DIR}/rclone.conf
LOCAL_KEEP_DAYS=7
REMOTE_DAILY_KEEP_DAYS=30
REMOTE_MONTHLY_KEEP_DAYS=800
EOF
  chmod 0600 "${CONFIG_FILE}"
}

# Created once and never replaced: archives written with an earlier passphrase must stay readable.
write_passphrase_file() {
  if [[ -s "${PASSPHRASE_FILE}" ]]; then
    return
  fi
  (umask 077; openssl rand -base64 36 > "${PASSPHRASE_FILE}")
  chmod 0600 "${PASSPHRASE_FILE}"
  PASSPHRASE_CREATED=1
}

write_units() {
  cat > "${SERVICE_FILE}" <<EOF
[Unit]
Description=EnergyFlowPilot off-site backup
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
Environment=APP_NAME=${APP_NAME}
ExecStart=${BACKUP_SCRIPT}
Nice=19
IOSchedulingClass=idle
EOF

  cat > "${TIMER_FILE}" <<EOF
[Unit]
Description=EnergyFlowPilot nightly off-site backup

[Timer]
OnCalendar=${BACKUP_SCHEDULE}
RandomizedDelaySec=600
Persistent=true

[Install]
WantedBy=timers.target
EOF

  chmod 0644 "${SERVICE_FILE}" "${TIMER_FILE}"
  systemctl daemon-reload
  systemctl enable --now "${APP_NAME}-backup.timer"
}

print_summary() {
  cat <<EOF

Naechtliche Sicherung installiert:

- Zeitplan:      ${BACKUP_SCHEDULE} (Timer ${APP_NAME}-backup.timer)
- Einstellungen: ${CONFIG_FILE}
- Passwort:      ${PASSPHRASE_FILE}
- Protokoll:     journalctl -u ${APP_NAME}-backup.service

Naechste Schritte:
1. Cloud-Ziel einrichten:  sudo rclone config --config ${CONFIG_DIR}/rclone.conf
2. Ziel eintragen:         RCLONE_REMOTE in ${CONFIG_FILE}
3. Probelauf:              sudo systemctl start ${APP_NAME}-backup.service
EOF
  if [[ "${PASSPHRASE_CREATED:-0}" == "1" ]]; then
    cat <<EOF

WICHTIG: Es wurde ein neues Sicherungspasswort erzeugt. Ohne dieses Passwort ist keine Sicherung lesbar.
Jetzt in den Passwortmanager uebernehmen:  sudo cat ${PASSPHRASE_FILE}
EOF
  fi
}

main() {
  require_root
  validate_environment
  write_config_file
  write_passphrase_file
  write_units
  print_summary
}

main "$@"
