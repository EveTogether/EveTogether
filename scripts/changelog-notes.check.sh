#!/usr/bin/env bash
# Runs scripts/changelog-notes.sh against small CHANGELOG fixtures. Exits non-zero on the first mismatch.
set -euo pipefail

script="$(dirname "$0")/changelog-notes.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

check() {
    local name="$1" expected="$2" actual="$3"
    if [ "$expected" != "$actual" ]; then
        printf 'FAIL %s\n--- expected\n%s\n--- actual\n%s\n' "$name" "$expected" "$actual" >&2
        exit 1
    fi
    echo "ok   $name"
}

cat > "$work/previous.md" <<'MD'
# Changelog

## [Unreleased]

- **Fixed: alpha.** Alpha is fixed.
- **Added: bravo.** Bravo spans
  two lines.
- **Changed: charlie.** Charlie is changed.

## v1.0.0 — 2026-01-01

- **Added: old release.** Only in a release.
MD

cat > "$work/current.md" <<'MD'
# Changelog

## [Unreleased]

- **Added: delta.** A brand new entry.
- **Fixed: alpha.** Alpha is fixed.
- **Added: bravo.** Bravo spans two lines,
  and now says more.
- **Changed: charlie.** Charlie is changed.

### Fixed
- **Fixed: echo.** Under a heading,
  across
  three lines.

## v1.0.0 — 2026-01-01

- **Added: old release.** Only in a release.
MD

check "nightly lists only new and rewritten bullets, whole" \
"nightly-20260924.abc1234.5

- Added: delta. A brand new entry.
- Added: bravo. Bravo spans two lines, and now says more.
- Fixed: echo. Under a heading, across three lines." \
"$(bash "$script" nightly nightly-20260924.abc1234.5 "$work/current.md" "$work/previous.md")"

check "nightly re-wrapped but unchanged bullet is not new" \
"label

No player-visible changes since the previous nightly." \
"$(bash "$script" nightly label "$work/previous.md" "$work/previous.md")"

check "nightly without a previous build takes the top bullets" \
"label

- Added: delta. A brand new entry.
- Fixed: alpha. Alpha is fixed." \
"$(sed 's/^FALLBACK_BULLETS=.*/FALLBACK_BULLETS=2/' "$script" > "$work/limited.sh"; bash "$work/limited.sh" nightly label "$work/current.md")"

cat > "$work/versions.md" <<'MD'
## [Unreleased]

- **Added: unreleased.** Not shipped.

## v0.2.10 — 2026-02-01

- **Added: ten.** Belongs to v0.2.10.

## v0.2.1 — 2026-01-01

Intro paragraph
over two lines.

### Added
- **Added: one.** Belongs
  to v0.2.1.
MD

check "release matches its own heading, not a longer version" \
"Intro paragraph over two lines.
- Added: one. Belongs to v0.2.1." \
"$(bash "$script" release "$work/versions.md" v0.2.1)"

check "release accepts the unreleased heading" \
"- Added: unreleased. Not shipped." \
"$(bash "$script" release "$work/versions.md" "[Unreleased]")"

if bash "$script" release "$work/versions.md" v9.9.9 2> /dev/null; then
    echo "FAIL release with a missing section must fail" >&2
    exit 1
fi
echo "ok   release with a missing section fails"
