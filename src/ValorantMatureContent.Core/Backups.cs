using System.Text.Json;

namespace ValorantMatureContent.Core;

public sealed record BackupFile(string RelativePath, string Sha256, long Size);
public sealed record BackupMetadata(
    int BackupVersion,
    DateTimeOffset CreatedAt,
    string? GameVersion,
    string ToolVersion,
    string InstallationPath,
    IReadOnlyList<BackupFile> Files);
public sealed record BackupRecord(string DirectoryPath, BackupMetadata Metadata);

public interface IBackupService
{
    Task<OperationResult<BackupRecord>> CreateAsync(
        string installationPath,
        string? gameVersion,
        string toolVersion,
        bool markAsLatest = true,
        CancellationToken cancellationToken = default);
    Task<OperationResult<BackupRecord>> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ValidateAsync(BackupRecord backup, CancellationToken cancellationToken = default);
}

public sealed class BackupService(IFileSystem fileSystem, IHashService hashService, string backupRoot) : IBackupService
{
    private const string MetadataFileName = "backup.json";
    private const string LatestFileName = "latest.txt";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<OperationResult<BackupRecord>> CreateAsync(
        string installationPath,
        string? gameVersion,
        string toolVersion,
        bool markAsLatest = true,
        CancellationToken cancellationToken = default)
    {
        var directoryName = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var directory = Path.Combine(backupRoot, directoryName);
        var filesDirectory = Path.Combine(directory, "files");
        var paksPath = Path.Combine(installationPath, GameFiles.PaksRelativePath);

        try
        {
            fileSystem.CreateDirectory(filesDirectory);
            var files = new List<BackupFile>();
            foreach (var name in GameFiles.Original.Concat(GameFiles.Mature))
            {
                var source = Path.Combine(paksPath, name);
                if (!fileSystem.FileExists(source))
                {
                    continue;
                }

                var destination = Path.Combine(filesDirectory, name);
                await fileSystem.CopyFileAsync(source, destination, false, cancellationToken);
                var hash = await hashService.ComputeSha256Async(destination, cancellationToken);
                files.Add(new BackupFile(name, hash, fileSystem.GetFileLength(destination)));
            }

            var metadata = new BackupMetadata(1, DateTimeOffset.UtcNow, gameVersion, toolVersion, Path.GetFullPath(installationPath), files);
            await fileSystem.WriteAllTextAsync(Path.Combine(directory, MetadataFileName), JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken);
            var record = new BackupRecord(directory, metadata);
            var validation = await ValidateAsync(record, cancellationToken);
            if (!validation.IsSuccess)
            {
                return OperationResult.Failure<BackupRecord>(validation.ErrorCode, validation.Message, [.. validation.Details]);
            }

            if (markAsLatest)
            {
                fileSystem.CreateDirectory(backupRoot);
                await fileSystem.WriteAllTextAsync(Path.Combine(backupRoot, LatestFileName), directoryName, cancellationToken);
            }

            return OperationResult.Success(record, "Backup created and verified.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Failure<BackupRecord>(ErrorCode.BackupFailed, "Failed to create backup. No game files were changed.", exception.Message);
        }
    }

    public async Task<OperationResult<BackupRecord>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var latestPath = Path.Combine(backupRoot, LatestFileName);
            if (!fileSystem.FileExists(latestPath))
            {
                return OperationResult.Failure<BackupRecord>(ErrorCode.InvalidState, "No backup is available.");
            }

            var directoryName = (await fileSystem.ReadAllTextAsync(latestPath, cancellationToken)).Trim();
            if (Path.GetFileName(directoryName) != directoryName)
            {
                return OperationResult.Failure<BackupRecord>(ErrorCode.BackupFailed, "The latest backup pointer is invalid.");
            }

            var directory = Path.Combine(backupRoot, directoryName);
            var metadataPath = Path.Combine(directory, MetadataFileName);
            if (!fileSystem.FileExists(metadataPath))
            {
                return OperationResult.Failure<BackupRecord>(ErrorCode.BackupFailed, "Backup metadata is missing.");
            }

            var json = await fileSystem.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<BackupMetadata>(json, JsonOptions);
            if (metadata is null || metadata.BackupVersion != 1)
            {
                return OperationResult.Failure<BackupRecord>(ErrorCode.BackupFailed, "Backup metadata is invalid or unsupported.");
            }

            var record = new BackupRecord(directory, metadata);
            var validation = await ValidateAsync(record, cancellationToken);
            return validation.IsSuccess
                ? OperationResult.Success(record, "Backup loaded and verified.")
                : OperationResult.Failure<BackupRecord>(validation.ErrorCode, validation.Message, [.. validation.Details]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Failure<BackupRecord>(ErrorCode.BackupFailed, "Backup could not be loaded.", exception.Message);
        }
    }

    public async Task<OperationResult> ValidateAsync(BackupRecord backup, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in backup.Metadata.Files)
        {
            if (Path.GetFileName(file.RelativePath) != file.RelativePath || !names.Add(file.RelativePath) ||
                !GameFiles.Original.Concat(GameFiles.Mature).Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                return OperationResult.Failure(ErrorCode.BackupFailed, "Backup metadata contains an invalid file path.", file.RelativePath);
            }

            var path = Path.Combine(backup.DirectoryPath, "files", file.RelativePath);
            if (!fileSystem.FileExists(path) || fileSystem.GetFileLength(path) != file.Size || !await hashService.VerifyAsync(path, file.Sha256, cancellationToken))
            {
                return OperationResult.Failure(ErrorCode.BackupFailed, "Backup integrity verification failed.", file.RelativePath);
            }
        }

        return OperationResult.Success("Backup integrity is valid.");
    }
}

public interface IRestoreService
{
    Task<OperationResult> RestoreAsync(BackupRecord backup, string installationPath, CancellationToken cancellationToken = default);
}

public sealed class RestoreService(IFileSystem fileSystem, IHashService hashService, IBackupService backupService) : IRestoreService
{
    public async Task<OperationResult> RestoreAsync(BackupRecord backup, string installationPath, CancellationToken cancellationToken = default)
    {
        var validation = await backupService.ValidateAsync(backup, cancellationToken);
        if (!validation.IsSuccess)
        {
            return validation;
        }

        var paksPath = Path.Combine(installationPath, GameFiles.PaksRelativePath);
        var staged = new List<(string Temporary, string Destination, BackupFile File)>();
        try
        {
            foreach (var file in backup.Metadata.Files)
            {
                var source = Path.Combine(backup.DirectoryPath, "files", file.RelativePath);
                var destination = Path.Combine(paksPath, file.RelativePath);
                var temporary = destination + $".{Guid.NewGuid():N}.vmce.tmp";
                await fileSystem.CopyFileAsync(source, temporary, false, cancellationToken);
                if (!await hashService.VerifyAsync(temporary, file.Sha256, cancellationToken))
                {
                    return OperationResult.Failure(ErrorCode.RestoreFailed, "A staged restore file failed verification.", file.RelativePath);
                }

                staged.Add((temporary, destination, file));
            }

            foreach (var name in GameFiles.Original.Concat(GameFiles.Mature))
            {
                var path = Path.Combine(paksPath, name);
                if (fileSystem.FileExists(path))
                {
                    fileSystem.DeleteFile(path);
                }
            }

            foreach (var item in staged)
            {
                fileSystem.MoveFile(item.Temporary, item.Destination, true);
            }

            foreach (var file in backup.Metadata.Files)
            {
                var restored = Path.Combine(paksPath, file.RelativePath);
                if (!await hashService.VerifyAsync(restored, file.Sha256, cancellationToken))
                {
                    return OperationResult.Failure(ErrorCode.RestoreFailed, "Restored files failed final verification.", file.RelativePath);
                }
            }

            return OperationResult.Success("Backup restored and verified.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Failure(ErrorCode.RestoreFailed, "Restore failed.", exception.Message);
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