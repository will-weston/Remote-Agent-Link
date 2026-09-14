# Remote Agent Link

An experimental Windows RDP dynamic-virtual-channel plugin that lets a local MCP client work with a remote desktop and remote GitHub Copilot sessions. Install the **same MSIX on both computers**. Windows App carries the connection; a separate per-user broker owns device access and durable work.

**Pre-release:** the implementation and deterministic checks do not establish compatibility with a shipped Windows App build. Real Windows App/Windows 365 activation, clean-machine packaged COM marshalling, and authenticated interactive Copilot handoff require the acceptance procedure in [installation](docs/installation.md). Do not deploy this as an unattended production control service.

**Required v1 gate still open:** managed Copilot delegation is implemented, but attaching to an existing interactive CLI remains disabled (`incompatible_runtime`) until a safe policy-preserving, exclusive-turn runtime contract is established.

## Components

| Component | Responsibility |
| --- | --- |
| Native RDP plugin | Modern factory or legacy COM activation, both out of process, with a `RALink.v1` client listener |
| Native transport DLL | Remote-session WTS channel and fragmentation handling |
| Broker | Authenticated session IPC, TLS over RDP, device-access switch, desktop control, Copilot tasks |
| MCP host | Official C# SDK, stdio, explicit per-process connection binding, actual PNG image blocks |
| Desktop worker | Isolated, bounded UI Automation provider execution |

The WinUI 3 app shows this device, connected computers, and connected MCP agents.
**Allow agents to use this device** defaults to **Yes**. There is no fingerprint
pairing or per-action broker approval. **No** blocks agent access, revokes desktop
control and requests cancellation of broker-owned coding work without closing
RDP. Closing the window keeps the app in the notification area.

This deliberately trusts the authenticated RDP session and signed-in Windows
user. Enabled agents can use the supported desktop and coding operations with
that user's rights; enable access only on devices and sessions you trust.

An agent cannot discover the tray process as an MCP server. **Setup** registers `ralink-mcp.exe` with Copilot CLI and keeps manual MCP configuration available. Each client starts its own stdio MCP process. Saved configuration and a connected agent are displayed separately.

All **32 MCP tools** are declared from startup. Binding, access changes and
disconnects never remove screenshot, input or agent tools from the catalog.
An invocation still requires the correct target, endpoint support and enabled
device access. No catalog refresh is needed after binding.

`rdp_list_connections` also reports **local RDP plugin instances without a
working DVC or remote broker**. These have `targetKind: "localRdpClient"`,
an opaque per-instance ID, and an independent lifecycle state. They can be
explicitly bound, but are currently **discovery-only**: local RDP-surface
capture/input is not implemented, and remote desktop/agent tools are unsupported
on these entries. Authenticated brokers remain separate `remoteEndpoint`
targets; binding never silently switches between them. See the
[public API contract gaps](src/Native/README.md#local-rdp-surface-control-contract-gap).
An optional public window-service probe now reports per-instance geometry and
failure diagnostics. A service HWND is not proof of exact connection identity;
background input and fresh minimized capture remain unsupported.

**Settings > RDP client loading** defaults to Modern. On Windows 11, Legacy compatibility registers a per-user COM entry for compatible classic RDP clients, without loading a plugin DLL into the client. Restart the RDP client after switching; permissions and the remote endpoint are unchanged. See [loader selection and cleanup](docs/installation.md#modern-and-legacy-rdp-loading). Automatic legacy registration adds a narrowly scoped restricted capability whose Microsoft Store approval is still an open gate.

The first desktop surface covers screenshots, mouse/keyboard, windows, and accessibility. Copilot collaboration uses the official SDK, not terminal scraping. Browser automation, OCR, clipboard, direct shell/Python, Cloud PC provisioning, and non-Windows clients are not included.

## Build

Prerequisites: Windows x64, PowerShell 7.2+, the SDK selected by `global.json`, Visual Studio C++ desktop tools, and a Windows SDK with MakeAppx/SignTool.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
.\scripts\package.ps1 -DevSign -SkipBuild
```

The build uses Microsoft's public `dotnet-public` NuGet mirror; no internal source or feed is required. Outputs, certificates, and local state are ignored by git. Development signing does **not** install a certificate or trust a package.

## Use

1. Install and open Remote Agent Link on both computers. Device access starts at **Yes**; no pairing is needed.
2. Connect with Windows App. Authenticated broker peers appear on **Home**.
   MCP also discovers initialized local plugin instances independently of peers.
3. Use **Setup > Add to Copilot CLI**, or the MCP configuration below. Restart the agent to load it.

The agent selects the intended computer using `rdp_list_connections` and
`rdp_bind_connection`, observes its screen, then reserves input with
`rdp_acquire_control`. This does not open an approval dialog or preempt another
agent. Turn the device switch to **No** to stop access.

```json
{
  "mcpServers": {
    "remote-agent-link": {
      "type": "local",
      "command": "ralink-mcp.exe",
      "args": [],
      "tools": ["*"]
    }
  }
}
```

Each MCP process binds to one connection; use another process for another target. A remote CLI can explicitly use `["--current-session"]` instead. This does not bypass the device switch or allow disconnected desktop control. Never use it or a local shell as a fallback for a failed remote action.

To save a specific target in your MCP configuration, set `args` to `["--connection", "<connectionId>"]`, using the exact ID returned by `rdp_list_connections`. Its declarations remain available even when the endpoint is offline; the MCP process will never substitute another desktop. Reconnecting to the same Windows logon retains the target. After remote logoff or identity replacement, explicitly choose the new connection ID. `--connection` and `--current-session` cannot be combined.

RDP disconnect revokes desktop control. Only explicitly scoped unattended non-UI tasks may continue while the remote user stays logged in and device access remains enabled; reconnect retrieves existing task IDs and events rather than resubmitting prompts. The remote user's Copilot authentication remains remote.

See [installation and recovery](docs/installation.md), [architecture](docs/architecture.md), [protocol](docs/protocol.md), [API compatibility](docs/api-compatibility.md), and [security](docs/security.md).

## Contributing and reporting

Start with [CONTRIBUTING.md](CONTRIBUTING.md) for build commands and review
expectations. Follow the [code of conduct](CODE_OF_CONDUCT.md), and report
suspected vulnerabilities using [SECURITY.md](SECURITY.md), not a public bug
report. Maintainer-only GitHub settings are described in
[GitHub setup](docs/github-setup.md).

The project is [MIT-licensed](LICENSE); redistributed dependencies retain the
terms in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) and `licenses`.
