# Contributing to EVE Together

Thanks for your interest. EVE Together is a small project maintained by two people, so the bar for
contributions is high and the rules below are not negotiable. Reading them before you open a pull
request saves everyone time.

## Not writing code? Still useful

The high bar below applies to **code in pull requests**. A clear, reproducible bug report or a
well-argued feature idea through the issue templates clears no bar and is one of the most helpful things
you can send us.

## The one thing to read first

**All code rules live in [`AGENTS.md`](AGENTS.md)** — architecture, conventions, the Definition of
Done, the review dimensions and the anti-slop catalogue. It is the single source of truth and it
applies to humans and AI assistants equally. A pull request is judged against `AGENTS.md`, not
against whatever tool produced it.

## Pull request policy

We are not reviewers for poorly-instructed AI output. To keep the project maintainable:

- **PRs that have not been checked by the author are rejected outright.** If you used an AI assistant,
  you are responsible for the result: it must meet `AGENTS.md` in full, build with zero warnings, pass
  the tests, and read like code you would sign your name to. "My assistant generated it" is not a
  review.
- **PRs that exist to force an AI review or "another opinion" are closed without discussion** — the
  "my AI thinks this could be better" category. We decide direction; drive-by rewrites do not.
- **Stay in scope.** One PR, one topic. No unrequested refactors, renames or "while I was here"
  changes bundled in.

A PR that does not meet the bar is closed, not line-by-line reviewed. Don't take it personally —
it keeps a two-person project alive.

## Before you open a PR

1. **Open an issue first** for anything beyond a trivial fix, so we can agree the approach before you
   spend time on it.
2. **Follow `AGENTS.md`** to the letter — conventions, module structure, the no-MediatR/AutoMapper/
   FluentResults rule, error handling, and the language rule (all source is English).
3. **Build and test locally:**
   ```
   make build
   make test
   ```
   Zero warnings, all tests green. New behaviour needs a test; a regression fix needs a test that is
   red without the fix.
4. **Check the Definition of Done** in `AGENTS.md` §8 — error paths and edge cases, no secrets in the
   diff, no anti-slop signals.
5. **Write the commit message** in the project format (`AGENTS.md` §10): English, `- {type}: {description}`
   bullets, no summary line, no AI attribution / `Co-Authored-By`.

## Project principles you cannot change in a PR

These are settled (see `AGENTS.md` §1). A PR that works against them will be declined:

- Local-first, fully autonomous — no central server / no SaaS.
- Open source and data-minimising as a trust basis.
- Self-written code only — no code copied from EVE Workbench or other tools.
- Good ESI citizen; always on the latest ESI version.
- No global "active character" — explicit character choice per action.

## Branching & releases

Branch and release strategy is still being settled and will be documented here once decided. Until
then, ask in your issue which branch to target.

## Licence & EVE attribution

By contributing you agree your contribution is licensed under the project's **GNU AGPL-3.0** licence
(`LICENSE`). EVE-related material is used under CCP's developer terms; keep the CCP disclaimer intact
wherever EVE material or CCP marks appear (`AGENTS.md` §12).
