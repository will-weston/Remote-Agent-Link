# Remote Agent Link

An experimental Windows RDP dynamic-virtual-channel plugin that lets a local MCP client work with a remote desktop and remote GitHub Copilot sessions. Install the **same MSIX on both computers**. Windows App carries the connection; a separate per-user broker owns consent and durable work.

**Pre-release:** the implementation and deterministic checks do not establish compatibility with a shipped Windows App build. Real Windows App/Windows 365 activation, clean-machine packaged COM marshalling, and authenticated interactive Copilot handoff require the acceptance procedure in [installation](docs/installation.md). Do not deploy this as an unattended production control service.

**Required v1 gate still open:** managed Copilot delegation is implemented, but attaching to an existing interactive CLI remains disabled (`incompatible_runtime`) until a safe policy-preserving, exclusive-turn runtime contract is established.

## Components

| Component | Responsibility |
| --- | --- |
| Native RDP plugin | Modern factory or legacy COM activation, both out of process, with a `RALink.v1` client listener |
| Native transport DLL | Remote-session WTS channel and fragmentation handling |
| Broker | Local IPC, paired mutual TLS, human permissions, desktop control, Copilot tasks |
| MCP host | Official C# SDK, stdio, explicit per-process connection binding, actual PNG image blocks |
| Desktop worker | Isolated, bounded UI Automation provider execution |

The broker is a WinUI 3 desktop app with a Store-inspired navigation rail, a Home dashboard, device cards, and Agents, Activity and Settings pages. Channels connect automatically while the app is running. **Allow agents to use this desktop** enables approval requests, not control; turning it off stops and blocks agent observation/input here without disconnecting RDP or stopping separately approved coding tasks. Closing hides to the notification area; the tray provides Open, Activity, Block agent desktop access on this PC, and Quit.

An agent cannot discover the tray process as an MCP server. **Agents** registers `ralink-mcp.exe` with Copilot CLI and explains the checked profile, manual setup and client reload. Each client starts its own stdio MCP process; adding that configuration never grants permissions.

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

1. Install the package on both endpoints and explicitly start the broker in each logged-in user session.
2. The default `both` role handles local and remote connections automatically. Open Windows App, connect to the remote desktop, and review the device on **Home**. Compare full endpoint fingerprints through trusted UI and pair both sides.
3. Use a ready device's **Copy MCP configuration** action, or configure `ralink-mcp.exe` as below. With general configuration, discover connections using `rdp_list_connections` and explicitly call `rdp_bind_connection`.
4. Use **Review request** on the controlled device to approve observation, control, delegation, and unattended work separately.

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

Each MCP process binds to one connection; use another process for another target. A remote CLI can explicitly use `["--current-session"]` instead. This does not bypass the same consent arbiter or allow disconnected desktop control.

To save a specific target in your MCP configuration, set `args` to `["--connection", "<connectionId>"]`, using the exact ID returned by `rdp_list_connections`. Its tools load when that endpoint is available; the MCP process will never substitute another desktop, even if the configured endpoint is offline. Reconnecting to the same Windows logon retains the target. After remote logoff or identity replacement, explicitly choose the new connection ID. `--connection` and `--current-session` cannot be combined.

RDP disconnect revokes desktop access. Only separately preauthorized non-UI tasks may continue while the remote user stays logged in; reconnect retrieves existing task IDs and events rather than resubmitting prompts. The remote user's Copilot authentication remains remote.

See [installation and recovery](docs/installation.md), [architecture](docs/architecture.md), [protocol](docs/protocol.md), [API compatibility](docs/api-compatibility.md), and [security](docs/security.md).

## Contributing and reporting

Start with [CONTRIBUTING.md](CONTRIBUTING.md) for build commands and review
expectations. Follow the [code of conduct](CODE_OF_CONDUCT.md), and report
suspected vulnerabilities using [SECURITY.md](SECURITY.md), not a public bug
report. Maintainer-only GitHub settings are described in
[GitHub setup](docs/github-setup.md).

The project is [MIT-licensed](LICENSE); redistributed dependencies retain the
terms in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) and `licenses`.
