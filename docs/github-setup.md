# GitHub maintainer setup

Repository files configure workflows and community guidance. They do **not**
enable GitHub-hosted protections, paid features, or private reporting by
themselves. Apply the settings below with repository-admin access. Do not change
repository visibility as part of this setup.

## First push and main protection

Review the exact initial snapshot before committing:

```powershell
git diff --cached --check
git diff --cached --stat
git diff --cached --name-only
gh auth status
```

If GitHub authentication is missing, run `gh auth login` interactively. Do not
put a token in a script, repository file, or pasted diagnostic.

Create and push the reviewed initial commit to `main`, then wait for **Build
and package / Windows x64** to pass on GitHub. The workflow job's status-check
context is **Windows x64**. Local builds do not establish that the hosted runner
passed.

Only **after** the branch exists and this check has appeared, import
[the main ruleset](../.github/rulesets/main.json) through
**Settings > Rules > Rulesets > New ruleset > Import a ruleset**. Review the
preview before saving. If the repository's plan does not support rulesets, use
equivalent branch protection where available; do not imply a checked-in JSON
file is enforced.

The ruleset requires pull requests, resolved review threads, an up-to-date
**Windows x64** check from GitHub Actions, squash/linear history, and no force
pushes or branch deletion. It has no bypass actors. The expected integration ID
is GitHub Actions (`15368`); administrators can confirm it with
`gh api apps/github-actions --jq .id` before importing. Do not import a second
copy on every setup; edit the existing ruleset instead.

The initial approval count is deliberately **zero** for a solo maintainer:
GitHub does not allow approving your own PR. `CODEOWNERS` requests the
maintainer's review but does not independently enforce it. Add a second trusted
reviewer before requiring one approval, last-push approval, and code-owner
approval. Do not silently remove checks or add an always-bypass actor to work
around a blocked merge.

Repository merge settings should agree with the ruleset:

```powershell
gh repo edit will-weston/Remote-Agent-Link `
  --enable-squash-merge `
  --enable-merge-commit=false `
  --enable-rebase-merge=false `
  --enable-auto-merge=false `
  --delete-branch-on-merge
```

Never auto-merge dependency updates just because a bot created them. SDK/runtime,
native ABI and Windows App SDK changes need human review.

## Actions and security settings

| Setting | Recommended value |
| --- | --- |
| Default workflow token permissions | Read repository contents; do not allow Actions to create/approve PRs |
| Fork workflow approval | Require approval for outside collaborators; never send write tokens or secrets to untrusted PRs |
| Runner trust | GitHub-hosted ephemeral runners, not the live local/remote RDP VMs |
| Actions policy | Permit the reviewed official `actions/*` and `github/codeql-action` actions; require full-SHA pins where the policy supports it |
| Dependency graph and Dependabot alerts/security updates | Enable where available |
| Secret scanning and push protection | Enable where available; do not treat ignore rules as a credential scanner |
| Private vulnerability reporting | Enable for a public repository and confirm the SECURITY.md reporting link works |
| Default branch | `main` |

Check feature availability and billing before enabling GitHub Code Security or
GitHub Secret Protection for a private repository. This setup does not opt into
paid features.

**CodeQL** performs a manual Windows build of both C# and C++. **Dependency
review** rejects newly introduced known high/critical dependency advisories.
These workflows run for public repositories. For a private repository, first
enable the required code-security/dependency-graph features, then explicitly set
the repository Actions variable `ENABLE_CODE_SCANNING` to `true`. Otherwise
their jobs are skipped. Do not add skipped or not-yet-successful security checks
to the required-check ruleset; add them after verifying the hosted runs.

Dependabot proposes weekly GitHub Actions and NuGet updates. WinUI's modular
dependencies are grouped; the Copilot SDK/runtime compatibility contract still
requires coordinated manual review. Dependencies are pinned in project files;
the repository does not claim a locked, bit-for-bit reproducible restore across
different SDK/toolchain installations.

Workflows use `pull_request`, not `pull_request_target`, and do not interpolate
PR titles, bodies, or branch names into shell commands. No signing secret is
needed for CI. The signing regression suite creates only isolated temporary
test identities; CI packaging remains unsigned.

## Releases

`release.yml` supplies categories for GitHub-generated notes; it does not publish
a release. CI artifacts are explicitly unsigned, retained for 14 days, and are
not production installers.

Before a release, satisfy the documented [acceptance gates](installation.md#release-gates),
increment the package version, and sign through a trusted process using the
approved persistent identity. Never export its private key into Actions or
rotate it implicitly. Publish an explicitly approved package, digest and public
certificate through the chosen distribution channel. Use prerelease status
while the current compatibility and acceptance gates remain open.

Keep GitHub release notes and supported-version claims consistent with
[SECURITY.md](../SECURITY.md). Do not add badges or claims implying that hosted
checks, release acceptance or private reporting are active before verifying
them.

## References

- [Secure use of GitHub Actions](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions)
- [Managing repository rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/managing-rulesets-for-a-repository)
- [Private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/working-with-repository-security-advisories/configuring-private-vulnerability-reporting-for-a-repository)
- [Windows hosted runner contents](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)
