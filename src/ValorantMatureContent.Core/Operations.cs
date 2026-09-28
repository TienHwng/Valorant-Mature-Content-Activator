namespace ValorantMatureContent.Core;

public enum OperationKind
{
    Enable,
    Disable,
    Repair,
    Verify,
    Backup,
    Restore
}

public enum PlannedActionKind
{
    CreateBackup,
    Install,
    Delete,
    RestoreBackup,
    Verify
}

public sealed record PlannedAction(PlannedActionKind Kind, string Path, string? Source = null);
public sealed record OperationPlan(
    OperationKind Kind,
    string InstallationPath,
    string? GameVersion,
    bool IsAllowed,
    bool IsNoOp,
    string Summary,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<PlannedAction> Actions,
    LoadedPayload? Payload);

public interface IOperationPlanner
{
    Task<OperationPlan> PlanAsync(OperationKind kind, string? configuredPath, string payloadManifestPath, CancellationToken cancellationToken = default);
}

public sealed class OperationPlanner(IStatusService statusService, IBackupService backupService, IPayloadService payloadService) : IOperationPlanner
{
    public async Task<OperationPlan> PlanAsync(OperationKind kind, string? configuredPath, string payloadManifestPath, CancellationToken cancellationToken = default)
    {
        var status = await statusService.GetAsync(configuredPath, payloadManifestPath, cancellationToken);
        if (status.InstallationPath is null)
        {
            return Blocked(kind, configuredPath ?? string.Empty, status.GameVersion, "VALORANT installation was not found.");
        }

        if (kind is not OperationKind.Verify && status.IsGameRunning)
        {
            return Blocked(kind, status.InstallationPath, status.GameVersion, "VALORANT or Riot Client is currently running. Close it before modifying files.");
        }

        var payloadResult = await payloadService.LoadAndVerifyAsync(payloadManifestPath, cancellationToken);
        var payload = payloadResult.Value;
        var paksPath = Path.Combine(status.InstallationPath, GameFiles.PaksRelativePath);

        if (kind == OperationKind.Verify)
        {
            return new OperationPlan(kind, status.InstallationPath, status.GameVersion, true, true, $"State: {status.MatureContentState}; compatibility: {status.CompatibilityStatus}.", status.Problems, [new(PlannedActionKind.Verify, paksPath)], payload);
        }

        if (kind == OperationKind.Backup)
        {
            return Allowed(kind, status, "Create and verify a backup.", [new(PlannedActionKind.CreateBackup, paksPath)], payload);
        }

        if (kind is OperationKind.Disable or OperationKind.Restore)
        {
            if (status.MatureContentState == MatureContentState.Disabled && kind == OperationKind.Disable)
            {
                return NoOp(kind, status, "Mature content is already disabled.", payload);
            }

            var backup = await backupService.GetLatestAsync(cancellationToken);
            var hasOriginalSet = backup.Value?.Metadata.Files is { } backupFiles &&
                GameFiles.Original.All(name => backupFiles.Any(file => string.Equals(file.RelativePath, name, StringComparison.OrdinalIgnoreCase)));
            return backup.IsSuccess && hasOriginalSet
                ? Allowed(kind, status, "Restore the latest verified backup.", [new(PlannedActionKind.RestoreBackup, paksPath)], payload)
                : Blocked(kind, status.InstallationPath, status.GameVersion, backup.IsSuccess ? "The latest backup does not contain a complete original file set." : backup.Message);
        }

        if (kind == OperationKind.Repair)
        {
            if (status.MatureContentState is MatureContentState.Disabled or MatureContentState.Enabled)
            {
                return NoOp(kind, status, "The managed file state is already valid.", payload);
            }

            var originalsComplete = GameFiles.Original.All(name => status.Files.Any(file => file.Exists && string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (originalsComplete)
            {
                var repairActions = new List<PlannedAction> { new(PlannedActionKind.CreateBackup, paksPath) };
                repairActions.AddRange(GameFiles.Mature
                    .Where(name => status.Files.Any(file => file.Exists && string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .Select(name => new PlannedAction(PlannedActionKind.Delete, Path.Combine(paksPath, name))));
                repairActions.Add(new PlannedAction(PlannedActionKind.Verify, paksPath));
                return Allowed(kind, status, "Remove only conflicting mature-content files and preserve the complete original set.", repairActions, payload);
            }

            var backup = await backupService.GetLatestAsync(cancellationToken);
            return backup.IsSuccess
                ? Allowed(kind, status, "Restore the latest verified backup to repair the partial state.", [new(PlannedActionKind.RestoreBackup, paksPath)], payload)
                : Blocked(kind, status.InstallationPath, status.GameVersion, "Repair cannot infer missing original files and no verified backup is available.");
        }

        if (!payloadResult.IsSuccess || payload is null)
        {
            return Blocked(kind, status.InstallationPath, status.GameVersion, payloadResult.Message);
        }

        if (status.CompatibilityStatus != CompatibilityStatus.Supported)
        {
            return Blocked(kind, status.InstallationPath, status.GameVersion, "Unknown or unsupported game build. Patching is blocked.");
        }

        if (kind == OperationKind.Enable && status.MatureContentState == MatureContentState.Enabled)
        {
            return NoOp(kind, status, "Mature content is already enabled and verified.", payload);
        }

        if (kind == OperationKind.Enable && status.MatureContentState != MatureContentState.Disabled)
        {
            return Blocked(kind, status.InstallationPath, status.GameVersion, $"Enable requires a complete original state; current state is {status.MatureContentState}.");
        }

        var actions = new List<PlannedAction> { new(PlannedActionKind.CreateBackup, paksPath) };
        foreach (var file in payload.Manifest.Files)
        {
            var current = status.Files.FirstOrDefault(item => string.Equals(item.Name, file.Name, StringComparison.OrdinalIgnoreCase));
            if (kind == OperationKind.Enable || current?.IsValid != true)
            {
                actions.Add(new PlannedAction(PlannedActionKind.Install, Path.Combine(paksPath, file.Name), Path.Combine(payload.RootPath, file.Name)));
            }
        }

        foreach (var name in GameFiles.Original)
        {
            var current = status.Files.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (current.Exists)
            {
                actions.Add(new PlannedAction(PlannedActionKind.Delete, Path.Combine(paksPath, name)));
            }
        }

        actions.Add(new PlannedAction(PlannedActionKind.Verify, paksPath));
        return Allowed(kind, status, kind == OperationKind.Repair ? "Repair only invalid or conflicting managed files." : "Install the verified payload transactionally.", actions, payload);
    }

    private static OperationPlan Allowed(OperationKind kind, GameState status, string summary, IReadOnlyList<PlannedAction> actions, LoadedPayload? payload) =>
        new(kind, status.InstallationPath!, status.GameVersion, true, false, summary, [], actions, payload);

    private static OperationPlan NoOp(OperationKind kind, GameState status, string summary, LoadedPayload? payload) =>
        new(kind, status.InstallationPath!, status.GameVersion, true, true, summary, [], [], payload);

    private static OperationPlan Blocked(OperationKind kind, string path, string? version, params string[] blockers) =>
        new(kind, path, version, false, false, "Operation is unavailable.", blockers, [], null);
}

public interface IOperationExecutor
{
    Task<OperationResult> ExecuteAsync(OperationPlan plan, bool dryRun = false, CancellationToken cancellationToken = default);
}

public sealed class OperationExecutor(
    IFileSystem fileSystem,
    IHashService hashService,
    IBackupService backupService,
    IRestoreService restoreService,
    IGameProcessDetector processDetector,
    string toolVersion) : IOperationExecutor
{
    public async Task<OperationResult> ExecuteAsync(OperationPlan plan, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (!plan.IsAllowed)
        {
            return OperationResult.Failure(ErrorCode.InvalidState, plan.Summary, [.. plan.Blockers]);
        }

        if (plan.IsNoOp || dryRun)
        {
            return OperationResult.Success(dryRun ? "Dry run completed. No files were changed." : plan.Summary);
        }

        if (plan.Kind is not OperationKind.Verify && processDetector.IsRunning())
        {
            return OperationResult.Failure(ErrorCode.GameRunning, "VALORANT or Riot Client is running. No files were changed.");
        }

        if (plan.Kind == OperationKind.Verify)
        {
            return OperationResult.Success(plan.Summary);
        }

        if (plan.Kind == OperationKind.Backup)
        {
            var backup = await backupService.CreateAsync(plan.InstallationPath, plan.GameVersion, toolVersion, true, cancellationToken);
            return backup.IsSuccess ? OperationResult.Success(backup.Message) : OperationResult.Failure(backup.ErrorCode, backup.Message, [.. backup.Details]);
        }

        var safetyBackup = await backupService.CreateAsync(plan.InstallationPath, plan.GameVersion, toolVersion, plan.Kind == OperationKind.Enable, cancellationToken);
        if (!safetyBackup.IsSuccess || safetyBackup.Value is null)
        {
            return OperationResult.Failure(safetyBackup.ErrorCode, safetyBackup.Message, [.. safetyBackup.Details]);
        }

        OperationResult result;
        if (plan.Kind is OperationKind.Disable or OperationKind.Restore)
        {
            var target = await backupService.GetLatestAsync(cancellationToken);
            result = target.IsSuccess && target.Value is not null
                ? await restoreService.RestoreAsync(target.Value, plan.InstallationPath, cancellationToken)
                : OperationResult.Failure(target.ErrorCode, target.Message, [.. target.Details]);
        }
        else if (plan.Actions.Any(action => action.Kind == PlannedActionKind.RestoreBackup))
        {
            var target = await backupService.GetLatestAsync(cancellationToken);
            result = target.IsSuccess && target.Value is not null
                ? await restoreService.RestoreAsync(target.Value, plan.InstallationPath, cancellationToken)
                : OperationResult.Failure(target.ErrorCode, target.Message, [.. target.Details]);
        }
        else
        {
            result = await InstallAsync(plan, cancellationToken);
        }

        if (result.IsSuccess)
        {
            return result;
        }

        var rollback = await restoreService.RestoreAsync(safetyBackup.Value, plan.InstallationPath, cancellationToken);
        return rollback.IsSuccess
            ? OperationResult.Failure(result.ErrorCode, $"{result.Message} The original state was restored.", [.. result.Details])
            : OperationResult.Failure(ErrorCode.RollbackFailed, "The operation and rollback both failed. Run diagnostics before changing files again.", result.Message, rollback.Message);
    }

    private async Task<OperationResult> InstallAsync(OperationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Payload is null)
        {
            return OperationResult.Failure(ErrorCode.InvalidManifest, "A verified payload is required.");
        }

        var staged = new List<(string Temporary, string Destination, PayloadFile File)>();
        try
        {
            foreach (var action in plan.Actions.Where(action => action.Kind == PlannedActionKind.Install))
            {
                var manifestFile = plan.Payload.Manifest.Files.First(file => string.Equals(file.Name, Path.GetFileName(action.Path), StringComparison.OrdinalIgnoreCase));
                var temporary = action.Path + $".{Guid.NewGuid():N}.vmce.tmp";
                await fileSystem.CopyFileAsync(action.Source!, temporary, false, cancellationToken);
                if (!await hashService.VerifyAsync(temporary, manifestFile.Sha256, cancellationToken))
                {
                    return OperationResult.Failure(ErrorCode.PayloadHashMismatch, "A staged payload file failed verification.", manifestFile.Name);
                }

                staged.Add((temporary, action.Path, manifestFile));
            }

            foreach (var item in staged)
            {
                fileSystem.MoveFile(item.Temporary, item.Destination, true);
            }

            foreach (var action in plan.Actions.Where(action => action.Kind == PlannedActionKind.Delete))
            {
                if (fileSystem.FileExists(action.Path))
                {
                    fileSystem.DeleteFile(action.Path);
                }
            }

            if (plan.Kind == OperationKind.Repair && staged.Count == 0)
            {
                var paksPath = Path.Combine(plan.InstallationPath, GameFiles.PaksRelativePath);
                var originalsComplete = GameFiles.Original.All(name => fileSystem.FileExists(Path.Combine(paksPath, name)));
                var matureAbsent = GameFiles.Mature.All(name => !fileSystem.FileExists(Path.Combine(paksPath, name)));
                return originalsComplete && matureAbsent
                    ? OperationResult.Success("Repair completed and the original file state was verified.")
                    : OperationResult.Failure(ErrorCode.InstallFailed, "Repair did not produce a complete original file state.");
            }

            foreach (var file in plan.Payload.Manifest.Files)
            {
                var path = Path.Combine(plan.InstallationPath, GameFiles.PaksRelativePath, file.Name);
                if (!await hashService.VerifyAsync(path, file.Sha256, cancellationToken))
                {
                    return OperationResult.Failure(ErrorCode.InstallFailed, "Installed payload verification failed.", file.Name);
                }
            }

            return OperationResult.Success("Operation completed and installed files were verified.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Failure(ErrorCode.InstallFailed, "Failed to modify game files.", exception.Message);
        }
        finally
        {
            foreach (var item in staged.Where(item => fileSystem.FileExists(item.Temporary)))
            {
                fileSystem.DeleteFile(item.Temporary);
            }
        }
    }
}