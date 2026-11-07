#!/usr/bin/env bash
# Turns CHANGELOG.md into the plain-text notes the update screen shows and the release pages carry. The update
# screen renders plain text, so bold markers and headings are dropped and every bullet becomes one line.
#
#   changelog-notes.sh unreleased <changelog>
#   changelog-notes.sh release <changelog> <heading>
#   changelog-notes.sh section <changelog> <heading>
#   changelog-notes.sh nightly <build-label> <changelog> [<previous-changelog>]
#
# A release usually has hundreds of entries, far more than a release page (125,000 characters) or an argument
# list can carry. So `release` and `section` read a curated release-highlights/<heading>.md beside the changelog
# when there is one, and otherwise the changelog section itself. Either way the result is cut at a line boundary
# to NOTES_LIMIT characters and closed with a link to the complete CHANGELOG.md at that tag; a curated file always
# gets the link, a section only when it was cut. The changelog section must exist either way, so a tag cannot be
# released without its entry.
set -euo pipefail

FALLBACK_BULLETS=10
NO_CHANGES="No player-visible changes since the previous nightly."
NOTES_LIMIT="${NOTES_LIMIT:-100000}"
REPO_URL="${REPO_URL:-https://github.com/EveTogether/EveTogether}"

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

_full_changelog_link() {
    local ref="$1"
    [ "$ref" = "[Unreleased]" ] && ref="main"
    printf 'Full list of changes: %s/blob/%s/CHANGELOG.md\n' "$REPO_URL" "$ref"
}

# Cuts stdin before the line that would take it past the limit and writes "cut" to the file named by $1 when it
# did, so the caller knows to add the link.
_cap() {
    awk -v limit="$NOTES_LIMIT" -v cutFlag="$1" '
        cut { next }
        { size += length($0) + 1; if (size > limit) { cut = 1; next } print }
        END { if (cut) print "cut" > cutFlag }
    '
}

# $1 changelog, $2 heading, $3 plain|markdown
_notes_of() {
    local changelog="$1" heading="$2" style="$3" highlights body cutFlag
    local dir
    dir=$(dirname "$changelog")
    highlights="$dir/release-highlights/$heading.md"

    if [ -z "$(_section "$changelog" "$heading" | _flatten)" ]; then
        echo "CHANGELOG.md has no '## $heading' section with notes." >&2
        return 1
    fi

    if [ -f "$highlights" ]; then
        body=$(tr -d '\r' < "$highlights")
    else
        body=$(_section "$changelog" "$heading")
    fi
    [ "$style" = plain ] && body=$(printf '%s\n' "$body" | _flatten)

    cutFlag=$(mktemp)
    printf '%s\n' "$body" | _cap "$cutFlag"
    if [ -f "$highlights" ] || [ -s "$cutFlag" ]; then
        [ "$style" = markdown ] && echo
        _full_changelog_link "$heading"
    fi
    rm -f "$cutFlag"
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
    release) _notes_of "$1" "$2" plain ;;
    section) _notes_of "$1" "$2" markdown ;;
    nightly) _nightly "$@" ;;
    *)
        echo "usage: $0 unreleased|release|section|nightly ..." >&2
        exit 2
        ;;
esac
