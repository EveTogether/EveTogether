#!/usr/bin/env bash
#
# Removes the LAST migration from the relevant migration stacks (per context +
# provider). Useful to roll back a partially/incorrectly added migration.
#
# Uses --force so it works OFFLINE: 'migrations remove' normally connects to the
# database to check whether the migration has already been applied, and refuses
# when the server is unreachable. With --force only the migration files are
# rolled back, without a DB check. Safe for a just-added, NOT YET deployed
# migration. Do NOT use it when the last migration is already on a (production) DB.
#
# Usage:
#   scripts/remove-migration.sh [scope]
#     scope = all (default) | client | server
#
set -uo pipefail

SCOPE="${1:-all}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

CLIENT_STACKS=(
  "EveUtils.Migrations.Client.Sqlite|ClientDbContext"
)
SERVER_STACKS=(
  "EveUtils.Migrations.Server.Sqlite|ServerDbContext"
  "EveUtils.Migrations.Server.MySql|ServerDbContext"
  "EveUtils.Migrations.Server.SqlServer|ServerDbContext"
  "EveUtils.Migrations.Server.PostgreSql|ServerDbContext"
)

case "$SCOPE" in
  all)    STACKS=("${CLIENT_STACKS[@]}" "${SERVER_STACKS[@]}") ;;
  client) STACKS=("${CLIENT_STACKS[@]}") ;;
  server) STACKS=("${SERVER_STACKS[@]}") ;;
  *) echo "Unknown scope '$SCOPE' (use all|client|server)" >&2; exit 1 ;;
esac

if ! dotnet ef --version >/dev/null 2>&1; then
  echo "ERROR: 'dotnet ef' not found. Install: dotnet tool install --global dotnet-ef" >&2
  exit 1
fi

echo "==> Removing last migration (scope: $SCOPE) from ${#STACKS[@]} stack(s)"

declare -a OK=()
declare -a FAIL=()

for entry in "${STACKS[@]}"; do
  PROJ="${entry%%|*}"
  CTX="${entry##*|}"
  echo ""
  echo "----- $PROJ ($CTX) -----"
  if dotnet ef migrations remove --force \
       --context "$CTX" \
       --project "$ROOT/$PROJ" \
       --startup-project "$ROOT/$PROJ"; then
    OK+=("$PROJ")
  else
    FAIL+=("$PROJ")
  fi
done

echo ""
echo "=================================================="
echo "Succeeded : ${OK[*]:-none}"
if (( ${#FAIL[@]} > 0 )); then
  echo "FAILED    : ${FAIL[*]}" >&2
  exit 1
fi
echo "=================================================="
