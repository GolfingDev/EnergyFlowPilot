#!/usr/bin/env bash

set -euo pipefail

# Checks the configured Git branch for new commits and runs update-linux.sh only
# when the local checkout is behind the remote branch.
#
# Intended cron usage:
#   */5 * * * * APP_NAME=energyflowpilot GIT_BRANCH=develop /path/to/repo/scripts/deploy/auto-update-linux.sh
#
# Optional environment overrides:
#   APP_NAME=energyflowpilot
#   SERVICE_NAME=energyflowpilot-api.service
#   PUBLISH_RUNTIME=linux-arm64
#   BACKEND_PORT=5094
#   GIT_BRANCH=develop

APP_NAME="${APP_NAME:-energyflowpilot}"
GIT_BRANCH="${GIT_BRANCH:-develop}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
UPDATE_SCRIPT="${SCRIPT_DIR}/update-linux.sh"

LOG_DIR="${LOG_DIR:-/var/log/${APP_NAME}}"
LOG_FILE="${LOG_FILE:-${LOG_DIR}/auto-update.log}"
LOCK_FILE="${LOCK_FILE:-/var/lock/${APP_NAME}-auto-update.lock}"

require_root() {
  if [[ "${EUID}" -ne 0 ]]; then
    echo "Dieses Auto-Update-Skript muss mit Root-Rechten ausgefuehrt werden." >&2
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

prepare_logging() {
  install -d -m 0755 "${LOG_DIR}"
  touch "${LOG_FILE}"
  chmod 0644 "${LOG_FILE}"

  exec >>"${LOG_FILE}" 2>&1
}

log() {
  printf '[%s] %s\n' "$(date --iso-8601=seconds)" "$*"
}

validate_environment() {
  require_command flock
  require_command git

  if [[ ! -d "${REPO_ROOT}/.git" ]]; then
    echo "Kein Git-Repository gefunden: ${REPO_ROOT}" >&2
    exit 1
  fi

  if [[ ! -f "${UPDATE_SCRIPT}" ]]; then
    echo "Update-Skript nicht gefunden: ${UPDATE_SCRIPT}" >&2
    exit 1
  fi
}

validate_clean_worktree() {
  if [[ -n "$(git -C "${REPO_ROOT}" status --porcelain)" ]]; then
    log "Lokale Aenderungen gefunden. Auto-Update wird uebersprungen."
    git -C "${REPO_ROOT}" status --short
    exit 0
  fi
}

remote_ref() {
  printf 'origin/%s' "${GIT_BRANCH}"
}

fetch_remote_branch() {
  git config --global --add safe.directory "${REPO_ROOT}" >/dev/null 2>&1 || true
  git -C "${REPO_ROOT}" fetch --prune origin "+refs/heads/${GIT_BRANCH}:refs/remotes/origin/${GIT_BRANCH}"
}

has_remote_update() {
  local target_ref
  target_ref="$(remote_ref)"

  if ! git -C "${REPO_ROOT}" rev-parse --verify --quiet "${target_ref}" >/dev/null; then
    log "Remote-Branch nicht gefunden: ${target_ref}"
    exit 1
  fi

  if [[ "$(git -C "${REPO_ROOT}" rev-parse HEAD)" == "$(git -C "${REPO_ROOT}" rev-parse "${target_ref}")" ]]; then
    return 1
  fi

  if ! git -C "${REPO_ROOT}" merge-base --is-ancestor HEAD "${target_ref}"; then
    log "Lokaler Branch ist nicht per Fast-Forward auf ${target_ref} aktualisierbar. Auto-Update abgebrochen."
    git -C "${REPO_ROOT}" log --oneline --decorate --graph --max-count=12 HEAD "${target_ref}"
    exit 1
  fi

  return 0
}

run_update() {
  local current_revision
  local target_revision

  current_revision="$(git -C "${REPO_ROOT}" rev-parse --short HEAD)"
  target_revision="$(git -C "${REPO_ROOT}" rev-parse --short "$(remote_ref)")"

  log "Neue Commits gefunden: ${current_revision} -> ${target_revision}. Starte Deployment."
  GIT_BRANCH="${GIT_BRANCH}" bash "${UPDATE_SCRIPT}"
  log "Deployment abgeschlossen: $(git -C "${REPO_ROOT}" rev-parse --short HEAD)"
}

main() {
  require_root
  prepare_logging
  validate_environment

  exec 9>"${LOCK_FILE}"
  if ! flock -n 9; then
    log "Ein Auto-Update laeuft bereits. Ueberspringe diesen Lauf."
    exit 0
  fi

  log "Pruefe Git-Updates fuer Branch ${GIT_BRANCH}."
  validate_clean_worktree
  fetch_remote_branch

  if ! has_remote_update; then
    log "Keine neuen Commits gefunden."
    exit 0
  fi

  run_update
}

main "$@"
