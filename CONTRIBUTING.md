# Contributing

Remote Agent Link is a Windows x64 prerelease. Read the [README](README.md),
[architecture](docs/architecture.md), and [security model](docs/security.md)
before changing transport, permissions, or task ownership. Use
[private reporting](SECURITY.md) for suspected vulnerabilities.

## Development setup

Use Windows x64, PowerShell 7.2 or later, the .NET SDK selected by `global.json`,
Visual Studio with the C++ desktop workload, and a Windows SDK. The build scripts
discover MSVC and SDK tools. No private sibling repository, installed RDP plugin,
Copilot login, or trusted development certificate is required to build and run
the deterministic checks.

The **full native bridge suite needs the current user/session broker pipe
free**. Use a disposable development session with no running Remote Agent Link
broker or RDP plugin. The test deliberately refuses to replace a live broker;
do not terminate someone else's session or work just to run it. GitHub-hosted
CI starts without this application's live processes.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
.\scripts\package.ps1 -SkipBuild
```

The last command creates an **unsigned validation package**, not a release.
Tests include isolated temporary signing identities in the current user's
personal certificate store; they clean up their own identities and do not
install trust. Run signing tests as your normal Windows user, not an
administrator.

For an implementation change, use the smallest applicable existing selector:

```powershell
.\scripts\test.ps1 -Suite Core,Ui -SkipBuild
.\scripts\test.ps1 -Suite Desktop -SkipBuild
.\scripts\test.ps1 -Suite Copilot -SkipBuild
.\scripts\test.ps1 -Suite Mcp
.\scripts\test.ps1 -Suite Native
.\scripts\test.ps1 -Suite Signing -SkipBuild
```

Use `-SkipBuild` only after building the native/product dependencies affected by
the change. The managed test projects still build when run. These are executable
test runners, not a `dotnet test` test-adapter suite.

While a live broker is in use, run only the relevant managed/signing selectors
and the safe native subsets below. They are **not** a substitute for the full
native bridge suite in an idle session:

```powershell
.\artifacts\native\Release\Native.Tests.exe --startup-only
.\artifacts\native\Release\Native.Tests.exe --manifest-only
.\artifacts\native\Release\Native.Tests.exe --activation-only
```

## Changes and review

The activation-only subset exercises loader selection and COM creation without
opening the live transport pipe. UI tests exercise registry ownership only under
unique temporary test keys, never the installed plugin's preference or AddIns.

Create a focused branch from `main`, describe the intended behavior, add
regression coverage, and open a pull request using the template. Follow
`.editorconfig` and the conventions in neighboring code; avoid unrelated
reformatting. Use descriptive commits such as `fix: reject stale desktop grants`
or `docs: clarify MCP setup`. Squash merging keeps `main` linear.

Preserve bounded messages, explicit target binding, same-logon identity checks,
human-only approval, cancellation, and no automatic replay of uncertain
mutations. Never trade those invariants for a passing demo. Treat remote text,
pixels, files and model output as untrusted data.

For UI changes, exercise normal and minimum window sizes, keyboard navigation,
clear endpoint scoping, and notices/approval visibility. Distinguish model tests
from actual installed WinUI acceptance. Do not run live desktop actions, change
certificate trust, or submit model requests without explicit authorization.

Dependency updates are proposals, not auto-merge candidates.
`GitHub.Copilot.SDK` and the allowed runtime in
[the adapter](src/AgentLink.Copilot/README.md) are a compatibility pair: update
code, setup guidance and offline tests together. Review WinUI/Windows App SDK
modular versions as a compatible set. Keep action references pinned to complete
commit SHAs and retain a version comment.

## What belongs in Git

Commit source, tests, manifests, public documentation, and required license
notices. Do not commit generated packages/binaries, certificates or keys,
machine-local MCP settings, environment files, logs, dumps, task databases,
prompts, screenshots containing private data, or proprietary reference code.
Do not blanket-exempt test directories from credential scanning.

Keep the existing [MIT license](LICENSE) and third-party notices. New
dependencies or copied public material need compatible redistribution terms and
attribution. Never copy private client implementation into this repository.

Development signing deliberately reuses its existing identity and fails rather
than rotating it. Do not delete signing history to bypass that protection.
Package installation, trust, live RDP and release signing are separate from CI;
follow [installation and acceptance](docs/installation.md).

Maintainers: the one-time GitHub settings and first-push order are documented in
[GitHub setup](docs/github-setup.md).
