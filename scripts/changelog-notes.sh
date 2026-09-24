#!/usr/bin/env bash
# Turns CHANGELOG.md into the plain-text notes the update screen shows and the release pages carry. The update
# screen renders plain text, so bold markers and headings are dropped and every bullet becomes one line.
#
#   changelog-notes.sh unreleased <changelog>
#   changelog-notes.sh release <changelog> <heading>
#   changelog-notes.sh section <changelog> <heading>
#   changelog-notes.sh nightly <build-label> <changelog> [<previous-changelog>]
set -euo pipefail

FALLBACK_BULLETS=10
NO_CHANGES="No player-visible changes since the previous nightly."

# Matches the heading exactly: "v0.2.1" must not pick up "v0.2.10".
_section() {
    awk -v heading="$2" '
        { sub(/\r$/, "") }
        index($0, "## ") == 1 { inSection = ($0 == "## " heading) || index($0, "## " heading " ") == 1; next }
        inSection { print }
    ' "$1"
}

_flatten() {
    awk '
        function flush() {
            if (block == "") return
            gsub(/\*\*/, "", block)
            print block
            block = ""
        }
        { sub(/\r$/, "") }
        /^#/ || /^[[:space:]]*$/ { flush(); next }
        /^- / { flush(); block = $0; next }
        { line = $0; sub(/^[[:space:]]+/, "", line); block = (block == "") ? line : block " " line }
        END { flush() }
    '
}

_unreleased() {
    _section "$1" "[Unreleased]" | _flatten
}

_notes_of() {
    local notes
    notes=$(_section "$1" "$2" | _flatten)
    if [ -z "$notes" ]; then
        echo "CHANGELOG.md has no '## $2' section with notes." >&2
        return 1
    fi
    printf '%s\n' "$notes"
}

_nightly() {
    local label="$1" changelog="$2" previous="${3:-}" bullets

    if [ -n "$previous" ]; then
        bullets=$(grep -Fxvf <(_unreleased "$previous") <(_unreleased "$changelog") || true)
        [ -n "$bullets" ] || bullets="$NO_CHANGES"
    else
        bullets=$(_unreleased "$changelog" | awk -v limit="$FALLBACK_BULLETS" 'NR <= limit')
    fi

    printf '%s\n\n%s\n' "$label" "$bullets"
}

command="${1:-}"
[ $# -gt 0 ] && shift

case "$command" in
    unreleased) _unreleased "$1" ;;
    release) _notes_of "$1" "$2" ;;
    section)
        _notes_of "$1" "$2" > /dev/null
        _section "$1" "$2"
        ;;
    nightly) _nightly "$@" ;;
    *)
        echo "usage: $0 unreleased|release|section|nightly ..." >&2
        exit 2
        ;;
esac
