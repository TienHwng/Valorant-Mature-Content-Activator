# Valorant Mature Content Manager

A Windows desktop application and CLI for inspecting and safely managing the repository's mature-content payload. Both interfaces use the same .NET 8 Core library for detection, compatibility checks, backups, hashing, transactional changes, rollback, and diagnostics.

> [!CAUTION]
> Modifying game files may violate Riot Games' terms or regional rules and may stop working after any game update. This project does not bypass Vanguard, inject into processes, spoof signatures, or hide modifications. Use it at your own risk.

## Current Support Status

The bundled legacy payload is integrity-pinned, but its compatible VALORANT build is not documented in repository history. Its manifest therefore lists no supported builds. The manager reports an unknown/unsupported build and **will not enable it** until a maintainer verifies a build and updates the manifest.

This is intentional: unknown build means no patch.

## Features

- WPF dashboard and `vmce` CLI backed by one Core library
- Validated installation discovery plus manual path configuration
- Full original/mature file-set state detection
- Game process and executable version detection
- Versioned payload compatibility manifest and SHA-256 verification
- Local-app-data backups with metadata and integrity checks
- Staged, transactional enable/disable/repair with rollback
- Dry-run plans, JSON CLI output, diagnostics, and structured logs
- Windows CI and portable self-contained release packaging

## Screenshot

Screenshot placeholder: the WPF status dashboard will be captured for the first verified release.

## Installation

Download `ValorantMatureContentManager-vX.Y.Z-win-x64.zip` and `SHA256SUMS.txt` from GitHub Releases. Verify the ZIP's SHA-256, extract it, then run:

- `desktop/ValorantMatureContentManager.exe` for the GUI
- `cli/vmce.exe --help` for the CLI

Settings, logs, and backups are stored under `%LOCALAPPDATA%\ValorantMatureContentManager`, never beside the game.

## GUI Usage

The dashboard reports installation, game build, content state, payload, compatibility, and backup availability. Disabled actions explain why. Every modifying operation displays its exact shared-Core plan before confirmation and runs asynchronously.

Use the installation-path field when automatic detection cannot locate a nonstandard installation. The path is validated before it is saved.

## CLI Usage

```powershell
vmce status
vmce status --json
vmce enable --dry-run
vmce enable
vmce disable
vmce verify
vmce repair --dry-run
vmce backup
vmce restore
vmce diagnose --json
vmce open-paks
```

Common options are `--path <VALORANT directory>`, `--payload <manifest>`, `--json`, `--dry-run`, and `--help`.

## Enable

Enable is available only when the game is closed, the original four-file set is complete, the payload passes SHA-256 verification, and the detected build is explicitly supported. A verified backup is created before files are staged. Installed files are verified before originals are removed.

## Disable / Restore

Disable restores the latest verified backup. Restore metadata and every backed-up file are checked first. A second safety snapshot protects the pre-restore state.

## Repair

Repair is conservative. Valid states are no-ops. When a complete original set conflicts with mature files, only conflicting files are removed. Ambiguous partial/corrupt states require a verified backup; the manager does not invent missing game files.

## Updating VALORANT

Disable/restore before a game update. After updating, refresh status and wait for a payload manifest that explicitly supports the new build. Never add wildcard compatibility.

## Compatibility

Application, payload, and game versions are independent. See [Payload format](docs/payload-format.md). Binary game-derived assets may not be redistributable; future payload releases can be delivered separately through the verified payload interface.

## Troubleshooting and Diagnostics

See [Troubleshooting](docs/troubleshooting.md). Run `vmce diagnose` or use **Run diagnostics** in the GUI. Reports redact the current user-profile prefix and do not include credentials or Riot tokens. Logs use JSON Lines format.

## Development

Requirements: Windows and the .NET 8 SDK selected by `global.json`.

```powershell
dotnet restore ValorantMatureContentManager.sln
dotnet build ValorantMatureContentManager.sln
dotnet test ValorantMatureContentManager.sln
dotnet run --project src/ValorantMatureContent.Cli -- status
dotnet run --project src/ValorantMatureContent.Desktop
```

Read [Architecture](docs/architecture.md), [Contributing](CONTRIBUTING.md), and the [legacy script analysis](docs/legacy-script-analysis.md) before changing file operations.

## Disclaimer and License

This is an unofficial community project and is not endorsed by Riot Games. VALORANT and Riot Games are trademarks of their respective owner. No open-source license has been selected; redistribution rights for code and bundled binary assets must be decided before a public release.
