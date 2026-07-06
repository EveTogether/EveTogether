#!/usr/bin/env bash
#
# Adds the SAME migration to the migration stacks of all relevant contexts +
# providers in one run, so an entity change never misses a stack.
#
# Matrix:
#   client : ClientDbContext  -> SQLite
#   server : ServerDbContext  -> SQLite (dev), MySQL, SQL Server, PostgreSQL
#
# Usage:
#   scripts/add-migration.sh <MigrationName> [scope]
#     scope = all (default) | client | server
#
set -uo pipefail

if [[ $# -lt 1 || -z "${1:-}" ]]; then
  echo "Usage: $0 <MigrationName> [all|client|server]" >&2
  exit 1
fi

NAME="$1"
SCOPE="${2:-all}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# "project|context"
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

echo "==> Adding migration '$NAME' (scope: $SCOPE) to ${#STACKS[@]} stack(s)"

declare -a OK=()
declare -a FAIL=()

for entry in "${STACKS[@]}"; do
  PROJ="${entry%%|*}"
  CTX="${entry##*|}"
  echo ""
  echo "----- $PROJ ($CTX) -----"
  if dotnet ef migrations add "$NAME" \
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
  echo "" >&2
  echo "WARNING: inconsistent state. Fix the error and rerun, or roll back with" >&2
  echo "  scripts/remove-migration.sh $SCOPE" >&2
  exit 1
fi

echo "Remember to run 'dotnet build' before 'dotnet run' (stale migration DLL otherwise)."
echo "=================================================="
