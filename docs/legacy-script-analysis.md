# Legacy Batch Script Analysis

`Tools/ValorantProject.bat` is retained for history and must not be used for new installations.

The script:

- launches Riot Client immediately without a user action;
- hard-codes both the Riot installation and developer payload paths;
- assumes `VNGLogo-WindowsClient.*` absence means mature content is enabled;
- copies a filename wildcard without checking source integrity or command success;
- deletes original files even when copy may have failed;
- has no process lock check, game version, compatibility metadata, backup, restore, final verification, or rollback;
- always prints success after copy/delete commands;
- cannot safely disable or repair a partial state.

The .NET application replaces this workflow with validated discovery, full-set state inspection, SHA-256 manifests, game-build compatibility, immutable operation plans, verified backups, staged writes, final verification, and rollback. The script should be removed only after a verified application release and migration notice.