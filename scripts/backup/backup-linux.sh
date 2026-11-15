#!/usr/bin/env bash

set -euo pipefail

# Off-site backup of everything that cannot be recreated: settings, the data protection key ring, the sign-in
# environment file and all permanent history tables. Runs nightly from a systemd timer (install-backup-timer.sh).
#
# It deliberately does NOT copy the database file. A full copy of the running 1.3 GB database (sqlite3 .backup)
# drove the service into watchdog restarts on 2026-11-04. Instead every table is read on its own, with idle I/O
# priority, in a short read transaction; in WAL mode a reader never blocks the control loop's writes.
# The large tables that expire anyway (raw samples, decision log, events) are left out; see EXCLUDED_TABLES.
#
# The archive is encrypted before it leaves the Pi: it contains access tokens in plain text and the key ring that
# decrypts the RSCP credentials.
#
# Usage:
#   sudo ./scripts/backup/backup-linux.sh
#
# Settings come from /etc/energyflowpilot/backup.env (see docs/30-operations/backup-and-restore.md); every value
# can also be given as an environment variable.

APP_NAME="${APP_NAME:-energyflowpilot}"
CONFIG_FILE="${CONFIG_FILE:-/etc/${APP_NAME}/backup.env}"

if [[ -f "${CONFIG_FILE}" ]]; then
  # shellcheck disable=SC1090
  source "${CONFIG_FILE}"
fi

DATA_DIR="${DATA_DIR:-/var/lib/${APP_NAME}}"
DATABASE_PATH="${DATABASE_PATH:-${DATA_DIR}/tibber-victron-controller.db}"
KEY_RING_DIR="${KEY_RING_DIR:-${DATA_DIR}/data-protection-keys}"
ENV_FILE="${ENV_FILE:-/etc/${APP_NAME}/api.env}"
LOCAL_DIR="${LOCAL_DIR:-${DATA_DIR}/backups/offsite}"
PASSPHRASE_FILE="${PASSPHRASE_FILE:-/etc/${APP_NAME}/backup.passphrase}"
# rclone target such as "onedrive:EnergyFlowPilot-Backup". Empty: the archive stays on the Pi only.
RCLONE_REMOTE="${RCLONE_REMOTE:-}"
RCLONE_CONFIG="${RCLONE_CONFIG:-/etc/${APP_NAME}/rclone.conf}"
# Raw data with its own retention, far too large for a nightly dump. Every other table is backed up, so a table
# added later is included without touching this script.
EXCLUDED_TABLES="${EXCLUDED_TABLES:-LiveConsumptionSamples DecisionLogEntries DecisionLogReasons OperationalEvents}"
LOCAL_KEEP_DAYS="${LOCAL_KEEP_DAYS:-7}"
REMOTE_DAILY_KEEP_DAYS="${REMOTE_DAILY_KEEP_DAYS:-30}"
REMOTE_MONTHLY_KEEP_DAYS="${REMOTE_MONTHLY_KEEP_DAYS:-800}"
TABLE_TIMEOUT_SECONDS="${TABLE_TIMEOUT_SECONDS:-600}"

WORK_DIR=""
GPG_HOME=""

log() {
  printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"
}

fail() {
  log "FEHLER: $*" >&2
  exit 1
}

cleanup() {
  [[ -n "${WORK_DIR}" && -d "${WORK_DIR}" ]] && rm -rf "${WORK_DIR}"
  [[ -n "${GPG_HOME}" && -d "${GPG_HOME}" ]] && rm -rf "${GPG_HOME}"
  return 0
}

# Lowest CPU and I/O priority: the control loop must never wait for the backup.
low() {
  if command -v ionice >/dev/null 2>&1; then
    nice -n 19 ionice -c3 "$@"
  else
    nice -n 19 "$@"
  fi
}

query() {
  low timeout "${TABLE_TIMEOUT_SECONDS}" sqlite3 -readonly -bail "${DATABASE_PATH}" "$@"
}

validate_environment() {
  [[ "${EUID}" -eq 0 ]] || fail "Dieses Skript muss mit Root-Rechten ausgefuehrt werden."
  local tool
  for tool in sqlite3 gpg tar gzip timeout; do
    command -v "${tool}" >/dev/null 2>&1 || fail "Benoetigtes Programm fehlt: ${tool}"
  done
  [[ -f "${DATABASE_PATH}" ]] || fail "Datenbank nicht gefunden: ${DATABASE_PATH}"
  [[ -s "${PASSPHRASE_FILE}" ]] || fail "Passwortdatei fehlt oder ist leer: ${PASSPHRASE_FILE}"
  if [[ -n "${RCLONE_REMOTE}" ]]; then
    command -v rclone >/dev/null 2>&1 || fail "rclone ist nicht installiert, aber RCLONE_REMOTE ist gesetzt."
    [[ -f "${RCLONE_CONFIG}" ]] || fail "rclone-Konfiguration nicht gefunden: ${RCLONE_CONFIG}"
  fi
}

is_excluded() {
  local table="$1" excluded
  for excluded in ${EXCLUDED_TABLES}; do
    [[ "${excluded}" == "${table}" ]] && return 0
  done
  return 1
}

dump_database() {
  local target="$1" table rows
  mkdir -p "${target}/data"

  # The schema of ALL tables, also the excluded ones, so a restored database is complete and the application
  # starts on it. sqlite_master keeps columns added later inside the stored CREATE statement, in table order,
  # which is what the INSERT statements below rely on.
  query "SELECT sql || ';' FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name;" \
    > "${target}/schema.sql"
  [[ -s "${target}/schema.sql" ]] || fail "Das Datenbankschema konnte nicht gelesen werden."

  : > "${target}/tables.tsv"
  while IFS= read -r table; do
    [[ -n "${table}" ]] || continue
    [[ "${table}" =~ ^[A-Za-z0-9_]+$ ]] || fail "Unerwarteter Tabellenname: ${table}"
    if is_excluded "${table}"; then
      printf '%s\t%s\n' "${table}" "excluded" >> "${target}/tables.tsv"
      continue
    fi
    # One sqlite3 call per table: each is its own short read transaction, so the WAL can be checkpointed between
    # tables. The price is that tables are not from the same instant, which these tables do not need.
    query ".mode insert ${table}" "SELECT * FROM \"${table}\";" | gzip > "${target}/data/${table}.sql.gz"
    rows="$(query "SELECT count(*) FROM \"${table}\";")"
    printf '%s\t%s\n' "${table}" "${rows}" >> "${target}/tables.tsv"
  done < <(query "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;")

  grep -q -P '\t[0-9]+$' "${target}/tables.tsv" || fail "Es wurde keine Tabelle gesichert."
}

copy_secrets() {
  local target="$1"
  if [[ -d "${KEY_RING_DIR}" ]]; then
    cp -a "${KEY_RING_DIR}" "${target}/data-protection-keys"
  else
    log "Warnung: Schluesselbund nicht gefunden (${KEY_RING_DIR}); verschluesselte Einstellungen waeren nach einer Wiederherstellung unlesbar."
  fi
  if [[ -f "${ENV_FILE}" ]]; then
    cp -a "${ENV_FILE}" "${target}/api.env"
  fi
}

write_manifest() {
  local target="$1"
  {
    printf 'createdAtUtc=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    printf 'host=%s\n' "$(hostname)"
    printf 'database=%s\n' "${DATABASE_PATH}"
    printf 'sqlite=%s\n' "$(sqlite3 --version | cut -d' ' -f1)"
    printf 'excludedTables=%s\n' "${EXCLUDED_TABLES}"
  } > "${target}/manifest.txt"
}

encrypt_archive() {
  local source_dir="$1" archive="$2"
  GPG_HOME="$(mktemp -d)"
  chmod 700 "${GPG_HOME}"
  # Table data is already compressed, so gpg does not compress again (-z 0).
  tar -C "${source_dir}" -cf - . | low gpg --homedir "${GPG_HOME}" --batch --yes --quiet --pinentry-mode loopback \
    --passphrase-file "${PASSPHRASE_FILE}" --symmetric --cipher-algo AES256 -z 0 --output "${archive}"
  chmod 600 "${archive}"
  [[ -s "${archive}" ]] || fail "Das verschluesselte Archiv ist leer."
}

upload_archive() {
  local archive="$1" name
  name="$(basename "${archive}")"
  if [[ -z "${RCLONE_REMOTE}" ]]; then
    log "Warnung: RCLONE_REMOTE ist nicht gesetzt. Die Sicherung liegt nur auf dem Pi und schuetzt nicht vor einem Ausfall der SD-Karte."
    return 0
  fi

  low rclone --config "${RCLONE_CONFIG}" copyto "${archive}" "${RCLONE_REMOTE}/daily/${name}"
  [[ -n "$(rclone --config "${RCLONE_CONFIG}" lsf "${RCLONE_REMOTE}/daily/${name}")" ]] \
    || fail "Die hochgeladene Sicherung ist am Ziel nicht zu finden."
  # The first backup of a month is kept much longer, so an error noticed late can still be undone.
  if [[ "$(date -u '+%d')" == "01" ]]; then
    low rclone --config "${RCLONE_CONFIG}" copyto "${archive}" "${RCLONE_REMOTE}/monthly/${name}"
  fi
  # Old archives are removed only after the new one has arrived.
  rclone --config "${RCLONE_CONFIG}" delete --min-age "${REMOTE_DAILY_KEEP_DAYS}d" "${RCLONE_REMOTE}/daily"
  rclone --config "${RCLONE_CONFIG}" delete --min-age "${REMOTE_MONTHLY_KEEP_DAYS}d" "${RCLONE_REMOTE}/monthly" 2>/dev/null || true
  log "Hochgeladen nach ${RCLONE_REMOTE}/daily/${name}."
}

main() {
  validate_environment
  trap cleanup EXIT
  umask 077
  mkdir -p "${LOCAL_DIR}"
  chmod 700 "${LOCAL_DIR}"

  local name archive
  name="${APP_NAME}-backup-$(date -u '+%Y%m%d-%H%M%S')"
  archive="${LOCAL_DIR}/${name}.tar.gpg"
  WORK_DIR="$(mktemp -d "${LOCAL_DIR}/.work-XXXXXX")"

  log "Sicherung ${name} beginnt."
  dump_database "${WORK_DIR}"
  copy_secrets "${WORK_DIR}"
  write_manifest "${WORK_DIR}"
  encrypt_archive "${WORK_DIR}" "${archive}"
  log "Archiv erstellt: ${archive} ($(du -h "${archive}" | cut -f1))."

  upload_archive "${archive}"

  find "${LOCAL_DIR}" -maxdepth 1 -type f -name "${APP_NAME}-backup-*.tar.gpg" -mtime "+${LOCAL_KEEP_DAYS}" -delete
  printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "${name}.tar.gpg" > "${LOCAL_DIR}/last-success"
  log "Sicherung ${name} abgeschlossen."
}

main "$@"
