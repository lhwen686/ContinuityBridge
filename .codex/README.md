# Local environment integration

This project is suitable for Codex local environments. Shared entry points resolve
their checkout from the script location and contain no host addresses or secrets.
Setup only checks the host; it does not restore packages or install tools.

Use the desktop app's **Settings → Local environments** for this project and let
the current app generate its environment file under `.codex/`. Configure the
platform overrides below. No active environment TOML is supplied in this change:
the official page documents UI generation but does not specify a complete stable
file schema. This README does not activate automatic setup or toolbar actions.

| Entry | Windows | macOS | Linux |
| --- | --- | --- | --- |
| Setup override | `pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action setup` | `sh ./scripts/dev/macos.sh setup` | `sh ./scripts/dev/linux.sh setup` |
| Action: Checkout status | `pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action status` | `sh ./scripts/dev/macos.sh status` | `sh ./scripts/dev/linux.sh status` |
| Action: Host preflight | same as setup override | same as setup override | same as setup override |

Optional Windows-only action: `pwsh -NoProfile -File ./scripts/dev/windows.ps1 -Action test-core`.
Do not assign it as a default action on other operating systems.
PowerShell 7 (`pwsh`) must already be available; the script can also be invoked with
`powershell -NoProfile -File` if Windows PowerShell is the configured host shell.

Keep generic setup empty when all three platform overrides are configured.
Review the app-generated file before committing; use only relative commands.
Do not commit `.codex/config.toml`, host-specific `*.local.toml`, MCP credentials,
personal SDK paths, SSH destinations or signing identities. Shared generated files
under `.codex/environments/` are otherwise not ignored.

Worktrees need a reviewed initial commit and independent dependency preparation.
They do not inherit `.tools/`, `.env` or `.local/`; set `CONTINUITYBRIDGE_DOTNET`
in the host environment if an existing SDK is elsewhere. Never copy credentials
automatically. VPS operators can use shell scripts directly without Codex.

Reference (checked 2026-09-09): [OpenAI local environments](https://learn.chatgpt.com/docs/environments/local-environment).
