#!/usr/bin/env bash

set -euo pipefail

# Rebuilds a database from an archive written by backup-linux.sh, into a directory of its own.
# It never touches the running service or its database; moving the result into place is a separate, manual step
# (see docs/30-operations/backup-and-restore.md). The same script serves the restore drill on any Linux machine.
#
# Usage:
#   ./scripts/backup/restore-linux.sh <archive.tar.gpg> <empty-target-directory>
#
# The passphrase is asked for, or read from the file named in PASSPHRASE_FILE.

ARCHIVE="${1:-}"
TARGET_DIR="${2:-}"
PASSPHRASE_FILE="${PASSPHRASE_FILE:-}"
DATABASE_NAME="${DATABASE_NAME:-tibber-victron-controller.db}"

GPG_HOME=""

fail() {
  echo "FEHLER: $*" >&2
  exit 1
}

cleanup() {
  [[ -n "${GPG_HOME}" && -d "${GPG_HOME}" ]] && rm -rf "${GPG_HOME}"
  return 0
}

validate_arguments() {
  [[ -n "${ARCHIVE}" && -n "${TARGET_DIR}" ]] || fail "Aufruf: restore-linux.sh <archiv.tar.gpg> <leeres-zielverzeichnis>"
  [[ -f "${ARCHIVE}" ]] || fail "Archiv nicht gefunden: ${ARCHIVE}"
  local tool
  for tool in sqlite3 gpg tar gzip; do
    command -v "${tool}" >/dev/null 2>&1 || fail "Benoetigtes Programm fehlt: ${tool}"
  done
  mkdir -p "${TARGET_DIR}"
  [[ -z "$(ls -A "${TARGET_DIR}")" ]] || fail "Das Zielverzeichnis ist nicht leer: ${TARGET_DIR}"
}

decrypt_archive() {
  GPG_HOME="$(mktemp -d)"
  chmod 700 "${GPG_HOME}"
  local -a passphrase_args=()
  if [[ -n "${PASSPHRASE_FILE}" ]]; then
    [[ -s "${PASSPHRASE_FILE}" ]] || fail "Passwortdatei fehlt oder ist leer: ${PASSPHRASE_FILE}"
    passphrase_args=(--batch --pinentry-mode loopback --passphrase-file "${PASSPHRASE_FILE}")
  fi
  mkdir -p "${TARGET_DIR}/archive"
  gpg --homedir "${GPG_HOME}" --quiet "${passphrase_args[@]}" --decrypt "${ARCHIVE}" | tar -C "${TARGET_DIR}/archive" -xf -
  [[ -f "${TARGET_DIR}/archive/schema.sql" && -f "${TARGET_DIR}/archive/tables.tsv" ]] \
    || fail "Das Archiv enthaelt kein Schema oder keine Tabellenliste."
}

build_database() {
  local database="${TARGET_DIR}/${DATABASE_NAME}" table expected actual
  sqlite3 -bail "${database}" < "${TARGET_DIR}/archive/schema.sql"

  while IFS=$'\t' read -r table expected; do
    [[ -n "${table}" ]] || continue
    [[ "${table}" =~ ^[A-Za-z0-9_]+$ ]] || fail "Unerwarteter Tabellenname im Archiv: ${table}"
    if [[ "${expected}" == "excluded" ]]; then
      echo "  ${table}: nicht gesichert (bleibt leer)"
      continue
    fi
    # One transaction per table: without it SQLite syncs after every single row.
    { echo "BEGIN;"; gzip -dc "${TARGET_DIR}/archive/data/${table}.sql.gz"; echo "COMMIT;"; } | sqlite3 -bail "${database}"
    actual="$(sqlite3 "${database}" "SELECT count(*) FROM \"${table}\";")"
    [[ "${actual}" == "${expected}" ]] || fail "${table}: ${actual} Zeilen wiederhergestellt, ${expected} erwartet."
    echo "  ${table}: ${actual} Zeilen"
  done < "${TARGET_DIR}/archive/tables.tsv"

  [[ "$(sqlite3 "${database}" "PRAGMA integrity_check;")" == "ok" ]] || fail "Die wiederhergestellte Datenbank besteht die Integritaetspruefung nicht."
  # The application expects WAL; a database built from statements starts in rollback-journal mode.
  sqlite3 "${database}" "PRAGMA journal_mode=WAL;" >/dev/null
}

print_summary() {
  cat <<EOF

Wiederherstellung abgeschlossen (der laufende Dienst wurde nicht veraendert):

- Datenbank:      ${TARGET_DIR}/${DATABASE_NAME}
- Schluesselbund: ${TARGET_DIR}/archive/data-protection-keys
- Anmeldedatei:   ${TARGET_DIR}/archive/api.env
- Stand:          $(grep '^createdAtUtc=' "${TARGET_DIR}/archive/manifest.txt" | cut -d= -f2)

Rohmesswerte, Entscheidungslog und Betriebsereignisse sind nicht Teil der Sicherung und beginnen leer.
Die Uebernahme in den Betrieb steht in docs/30-operations/backup-and-restore.md.
EOF
}

main() {
  validate_arguments
  trap cleanup EXIT
  umask 077
  decrypt_archive
  build_database
  print_summary
}

main "$@"
