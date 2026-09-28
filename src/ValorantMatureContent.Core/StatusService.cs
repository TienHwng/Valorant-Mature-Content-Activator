namespace ValorantMatureContent.Core;

public interface IStateDetector
{
    Task<(MatureContentState State, IReadOnlyList<string> Problems, IReadOnlyList<FileState> Files)> DetectAsync(
        string installationPath,
        LoadedPayload? payload,
        CompatibilityStatus compatibility,
        CancellationToken cancellationToken = default);
}

public sealed class StateDetector(IFileSystem fileSystem, IHashService hashService) : IStateDetector
{
    public async Task<(MatureContentState State, IReadOnlyList<string> Problems, IReadOnlyList<FileState> Files)> DetectAsync(
        string installationPath,
        LoadedPayload? payload,
        CompatibilityStatus compatibility,
        CancellationToken cancellationToken = default)
    {
        var paksPath = Path.Combine(installationPath, GameFiles.PaksRelativePath);
        var problems = new List<string>();
        var files = new List<FileState>();
        var payloadLookup = payload?.Manifest.Files.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var name in GameFiles.Original.Concat(GameFiles.Mature))
        {
            var path = Path.Combine(paksPath, name);
            if (!fileSystem.FileExists(path))
            {
                files.Add(new FileState(name, false, null, null, null));
                continue;
            }

            var hash = await hashService.ComputeSha256Async(path, cancellationToken);
            var expected = payloadLookup?.GetValueOrDefault(name);
            files.Add(new FileState(name, true, fileSystem.GetFileLength(path), hash, expected is null ? null : string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase)));
        }

        var originalCount = files.Count(file => file.Exists && GameFiles.Original.Contains(file.Name, StringComparer.OrdinalIgnoreCase));
        var matureFiles = files.Where(file => GameFiles.Mature.Contains(file.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        var matureCount = matureFiles.Count(file => file.Exists);

        MatureContentState state;
        if (originalCount == GameFiles.Original.Length && matureCount == 0)
        {
            state = MatureContentState.Disabled;
        }
        else if (originalCount > 0 && matureCount > 0)
        {
            state = MatureContentState.Conflict;
            problems.Add("Original and mature-content files are both present.");
        }
        else if (matureCount == GameFiles.Mature.Length && originalCount == 0)
        {
            if (payload is null)
            {
                state = MatureContentState.Unknown;
                problems.Add("Installed files cannot be verified without a valid payload manifest.");
            }
            else if (matureFiles.Any(file => file.IsValid != true))
            {
                state = MatureContentState.Corrupted;
                problems.Add("One or more installed payload files failed SHA-256 verification.");
            }
            else
            {
                state = MatureContentState.Enabled;
            }
        }
        else if (originalCount == 0 && matureCount == 0)
        {
            state = MatureContentState.Corrupted;
            problems.Add("Neither a complete original file set nor mature-content payload exists.");
        }
        else
        {
            state = MatureContentState.Partial;
            problems.Add("The installation contains an incomplete file set.");
        }

        if (compatibility != CompatibilityStatus.Supported && state == MatureContentState.Enabled)
        {
            state = MatureContentState.Unsupported;
            problems.Add("The installed payload is not known to support this game build.");
        }

        return (state, problems, files);
    }
}

public interface IStatusService
{
    Task<GameState> GetAsync(string? configuredPath, string? payloadManifestPath, CancellationToken cancellationToken = default);
}

public sealed class StatusService(
    IInstallationDetector installationDetector,
    IGameVersionDetector versionDetector,
    IGameProcessDetector processDetector,
    IPayloadService payloadService,
    ICompatibilityService compatibilityService,
    IStateDetector stateDetector,
    IBackupService backupService) : IStatusService
{
    public async Task<GameState> GetAsync(string? configuredPath, string? payloadManifestPath, CancellationToken cancellationToken = default)
    {
        var installation = await installationDetector.DetectAsync(configuredPath, cancellationToken);
        if (!installation.IsSuccess || installation.Value is null)
        {
            return new GameState(null, null, MatureContentState.Unknown, CompatibilityStatus.Unknown, null, BackupStatus.Missing, processDetector.IsRunning(), [installation.Message], []);
        }

        LoadedPayload? payload = null;
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(payloadManifestPath))
        {
            var payloadResult = await payloadService.LoadAndVerifyAsync(payloadManifestPath, cancellationToken);
            if (payloadResult.IsSuccess)
            {
                payload = payloadResult.Value;
            }
            else
            {
                problems.Add(payloadResult.Message);
            }
        }

        var version = await versionDetector.DetectAsync(installation.Value, cancellationToken);
        var compatibility = payload is null ? CompatibilityStatus.Unknown : compatibilityService.Check(version, payload.Manifest);
        var detected = await stateDetector.DetectAsync(installation.Value, payload, compatibility, cancellationToken);
        var backup = await backupService.GetLatestAsync(cancellationToken);
        problems.AddRange(detected.Problems);
        if (version is null)
        {
            problems.Add("The game build could not be detected.");
        }

        return new GameState(installation.Value, version, detected.State, compatibility, payload?.Manifest.PayloadVersion, backup.IsSuccess ? BackupStatus.Available : BackupStatus.Missing, processDetector.IsRunning(), problems, detected.Files);
    }
}
