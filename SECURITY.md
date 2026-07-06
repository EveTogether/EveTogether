# Security policy

EVE Together handles EVE Online SSO tokens and character data, so we take security seriously.

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report a vulnerability privately through GitHub's
[**Report a vulnerability**](https://github.com/EveTogether/EveTogether/security/advisories/new)
(the repository's Security → Advisories tab).

We aim to acknowledge a report within a few business days and keep you posted as we work on a fix.
Please give us reasonable time to ship a fix before disclosing the issue publicly (coordinated disclosure).

When reporting, please include:
- the affected component (desktop client or self-hosted server),
- steps to reproduce,
- the impact you foresee.

## Scope

- **Desktop client** — local token storage, the EVE SSO / PKCE flow, the gRPC client and TOFU pinning.
- **Self-hosted server** — the confidential ESI exchange, pairing and session handling, the permission
  gate, and the Blazor control panel.

## Supported versions

The project is pre-1.0; security fixes target the latest release and `main`.
