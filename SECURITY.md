# Security policy

Remote Agent Link is experimental desktop-control software, not an OS sandbox
or an unattended production service. No stable release is currently supported.
Security fixes target current `main` and the next prerelease; old development
packages do not have a promised backport policy.

## Report privately

Use GitHub's [Report a vulnerability](https://github.com/will-weston/Remote-Agent-Link/security/advisories/new)
form when private vulnerability reporting is enabled. Do not file exploit
details in a public issue or pull request.

If the private form is unavailable, use the [Private security contact](https://github.com/will-weston/Remote-Agent-Link/issues/new?template=security_contact.yml)
issue template, with no vulnerability details, affected private system names,
or attachments. The maintainer can arrange a private reporting
channel before receiving sensitive information. There is no response-time SLA
for this prerelease project.

Include the affected revision/package, Windows/client versions, required
privileges, impact, and a minimal benign reproduction. Never include real
credentials, private keys, endpoint state, user prompts, or unredacted desktop
captures. Coordinate disclosure with the maintainer.

## Scope and expectations

Relevant boundaries include endpoint pairing, capability approval, desktop
ownership, Windows user/session/logon isolation, DVC framing, local IPC, task
ownership, and package/update integrity. Same-user full-trust compromise and
local administrators are outside the broker's isolation guarantee. That does
not make unexpected privilege or authorization bypasses acceptable.

See the [security model](docs/security.md) and
[acceptance limitations](docs/installation.md#release-gates). Report findings
against the actual supported behavior rather than assuming cloud-service
guarantees, a sandbox, or working interactive Copilot attachment.
