# ContinuityBridge development rules

## Scope and layout

- Read `docs/development-hosts.md` before cross-host work. Preserve `src/`, `tests/`, the solution and existing project names unless a task requires a change.
- Windows is the primary client development host: Win32 clipboard, tray, logon startup and Windows tests. Mac mini is the iPhone USB/Shortcuts UI test lab. Linux VPS is the future Cloud Relay deployment host.
- All current .NET projects target `net10.0-windows`; do not claim macOS/Linux runtime support. Cloud Relay is a future, separately authorized implementation, not the existing Windows API project.
- Environment preparation does not authorize business changes, SDK/tool installation, Mobile MCP/WDA installation, device pairing, service startup, Docker deployment or network configuration changes.

## Git and host isolation

- Use one shared remote repository with an independent local clone on each host. Never synchronize `.git`, working directories, `.tools`, credentials or build output using folder-sync software.
- Keep `main` stable. Use short-lived `feature/<topic>`, `fix/<topic>` or Codex worktrees; never maintain long-lived Windows/Mac branches.
- Before editing, inspect repository root, status, branch and remotes. Preserve all unrelated changes. No destructive checkout/reset/clean, history rewrite or force push without explicit authorization.
- A worktree belongs to its local host. Check out the same committed SHA on test hosts; report dirty working trees separately. Initial publication must review an explicit file allowlist; do not blindly `git add .`.
- This checkout initially had no Git metadata. Until the first reviewed commit exists, `main` is unborn and worktree/clone-based handoff is not ready. Do not invent an upstream or claim a GitHub connection.

## Host configuration and secrets

- Resolve paths from the checkout or script location. Follow `global.json`; on Windows prefer `CONTINUITYBRIDGE_DOTNET`, then checkout-local `.tools/dotnet/dotnet.exe`, then PATH. Worktrees do not inherit ignored tools.
- Store machine-specific settings under ignored `.local/` or in host environment variables; use the OS credential manager for secrets. Do not automatically load/copy local settings between checkouts.
- Never commit keys, tokens, passwords, cookies, device identifiers, signing profiles, private endpoint details, raw clipboard text, screenshots/UI trees or unsanitized logs. Use ignored `artifacts/` for test output; redact before writing shareable summaries.
- Ignore rules are not a secret scanner. Review staged content and run a current secret scan before initial publication or new credential integration.
- Existing ignored plans/reports remain local historical records. Read relevant local phase rules before implementing; prepare a reviewed sanitized shared plan before other hosts depend on it.

## Setup, actions and validation

- `scripts/dev/windows.ps1`, `macos.sh` and `linux.sh` default to read-only `setup`; `status` reports the current checkout. Missing tools must be reported, never installed automatically.
- Windows `test-core` is an explicit action using existing restore assets. It does not validate Win32 clipboard, tray, autostart, iPhone or relay behavior. Never run interactive/manual tests as automatic worktree setup.
- Preserve the existing phase gates. Record PASS, FAIL, BLOCKED and NOT RUN accurately; metadata checks, mocks or skipped tests do not establish real-device PASS.
- Before live clipboard/UI tests, obtain task scope for device interaction, use non-private fixtures and preserve user clipboard/browser state. Never infer a completed G2 matrix from partial evidence.
- Tailscale Serve/Funnel, public listeners, firewall and VPS changes require explicit task authorization. Setup must not execute these operations.

## Browser and computer control

- Use current official CUA/browser tools and their returned documentation. The former fix.6-only routing, fixed extension ID and hardcoded runtime paths are retired; do not reinstate them.
- Never launch, close or restart Chrome via scripts/flags, create temporary profiles, or alter/disable extensions. Preserve Cold Turkey Blocker and normal login. Provide manual UI steps for extension changes unless a specific safe operation is authorized.
- Never read cookies, passwords or session storage to bypass login. On a page failure, allow at most one evidence-based read-only recovery; inspect actual results before retrying a mutation.
- For missing tools/debugger failures, follow the current repair-codex-chrome skill. Do not fabricate native pipes or launch unofficial helpers; use official UI recovery when required.
- Any authorized runtime patch needs current version/hash, rollback copy and validation evidence. Do not apply an old patch after an unverified upgrade.

## Project memory

- Search codex-mem at the start of continuing/diagnostic work and before broad architecture/dependency/deployment changes. If unavailable or empty, say so and consult repository records.
- Save memory only when directly requested by the user and allowed by the active memory interface. Never save secrets or personal test evidence; do not claim a save that was not performed.
