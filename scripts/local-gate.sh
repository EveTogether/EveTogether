#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

base=${1:-origin/main}
head=${2:-HEAD}
changed=$(git diff --name-only "$base" "$head")

if echo "$changed" | grep -q '^CHANGELOG\.md$'; then
  exit 0
fi

if echo "$changed" | grep -Ev '(\.Tests?/|\.UiTests/|\.md$|^\.github/|^scripts/)' | grep -Eq '\.(cs|axaml)$'; then
  echo "This branch changes code but not CHANGELOG.md. If the change is user-visible, add a line to [Unreleased] (see AGENTS.md §10). Internal cleanup, tests and dependency bumps don't need one."
fi
