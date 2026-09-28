# Troubleshooting

## Installation Not Found

Select the VALORANT installation directory, not its `live` or `Paks` child. A valid selection contains `live\ShooterGame\Content\Paks`.

## Game Build Unknown or Unsupported

The manager cannot read a reliable file version or the payload manifest does not list it. Update VALORANT, refresh, and wait for a verified payload manifest. Do not bypass this check.

## Game Is Running

Close VALORANT and Riot Client, then refresh. The manager never kills either process automatically.

## Conflict, Partial, or Corrupted State

Run `vmce verify` and `vmce diagnose`. Use Repair when available. If files are missing and no verified backup exists, use the official Riot repair/update process; the manager will not synthesize original game files.

## Backup Invalid

Do not edit files under `%LOCALAPPDATA%\ValorantMatureContentManager\backups`. A hash mismatch blocks restore. Preserve diagnostics and logs before using Riot's official repair process.

## Operation and Rollback Failed

Stop modifying the installation. Export diagnostics and retain logs. Run Riot's official repair only after recording the actual filesystem state.