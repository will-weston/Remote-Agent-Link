# Security and consent

Remote Agent Link is **not an OS sandbox**. The broker, desktop APIs, and an authorized Copilot runtime execute with the remote user's normal rights. Same-user full-trust compromise and local administrators are outside its isolation guarantee.

## Authorization

- The automatic TLS channel proves possession of currently valid presented certificate keys. Unknown peers may exchange bounded hello/heartbeat metadata only; pin-based operation admission is a separate step. RDP encryption alone does not grant application authorization.
- First contact and changed endpoint certificates never grant tool access. Compare complete SHA-256 fingerprints through trusted UI and explicitly pair both sides. A peer-supplied name or readiness flag is not local authority.
- Endpoint keys live in the current user's Windows certificate store. Application state uses a user-only ACL. No global certificate trust changes are needed for pairing.
- Pairing, observation, desktop control, agent delegation, and unattended execution are separate decisions.
- Human approval is not a model-callable operation. Approval prompts show the actor and requested capability. Synthetic input pauses while the approval UI is active.
- Turning off the controlled endpoint's desktop-access switch, its tray block action, and Ctrl+Alt+Pause revoke and block this desktop's observation/control grants. RDP and separately approved non-UI work remain available. Re-enabling permits new approval requests; it never restores old grants. A normal agent release cannot release another agent's lease.
- Disconnect invalidates pending grants and never automatically restores them on reconnect.
- Pending channels are limited to 4-KiB frames, 2-KiB metadata objects and 32 frames per five seconds. They cannot invoke catalogs, desktop/agent operations, task APIs, local control requests, or approval actions.
- Status/logging observer failures are reported through bounded diagnostics and cannot interrupt revocation. A second-launch window request reports `ui_unavailable` when no control surface can accept delivery.

Desktop pixels, accessibility text, file contents, and other agents' messages are untrusted task data. They cannot authorize a tool, approve pairing, or change unattended policy. Do not approve a request merely because an on-screen document says to do so.

## Limits and ownership

Desktop operations must fail on unavailable/protected desktops, stale layout observations, ambiguous window selection, and denied input. Secure-desktop/UAC access, elevation, clipboard-backed paste, and credential extraction are not supported.

Non-UI continuation requires explicit preauthorization and finite task/workspace/tool limits. SDK credit caps are soft limits, not guaranteed hard billing ceilings. A disconnected RDP session may still be logged off by enterprise policy; the project never changes that policy.

An attached interactive CLI remains user-owned. Detach must not terminate it. A plain terminal is not automatically an attachable runtime, and MCP never accepts arbitrary runtime URLs or connection secrets.

No local GitHub token is copied to the remote machine. The remote runtime uses the remote user's existing authentication. Do not place runtime connection tokens, prompts, screenshots, signing keys, or private task outputs in source control.

## RDP client integration

RDP loading selection is not an authorization grant and is not exposed as an MCP
tool. Modern and legacy activation retain the same out-of-process transport,
pairing and consent boundaries. Legacy setup writes only the current user's
owned AddIns leaf, never HKLM or conventional COM registration. Conflicting
package-family ownership or a changed plugin value is not overwritten. Cleanup
preserves unrelated values and subkeys.

The MSIX declares `unvirtualizedResources` solely for a selective Windows 11
registry-write exclusion of that leaf; the loading preference and all other
registry/file-system virtualization remain unchanged. This is an additional
restricted-capability Store approval gate, not a general Store eligibility
claim. Excluded registry data can survive uninstall: switch to Modern and
resolve any cleanup warning before removing the package.

## Diagnostics and retention

Transport diagnostics record identities, method/error categories, and connection transitions rather than request payloads or screenshots. Durable task outputs may contain private code: retain them only as needed and treat exported diagnostics accordingly.

Development MSIX signing is distinct from endpoint pairing. The packaging script reuses an approved persistent Windows signing identity and fails before producing replacement outputs when its private key is missing or its history is inconsistent. A genuinely first-time development identity retains a non-exportable key in the Windows user certificate store; only its public certificate is exported. A public `.cer` cannot recover a discarded private key. The script never rotates an established signer or installs trust automatically. See [signing and recovery](installation.md#build-and-signing).
