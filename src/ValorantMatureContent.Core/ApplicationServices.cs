using System.Reflection;
using System.Text.Json;

namespace ValorantMatureContent.Core;

public sealed record AppSettings(string? ManualInstallationPath, bool CheckForUpdates, string Language, AppLogLevel LogLevel)
{
    public static AppSettings Default { get; } = new(null, true, "en", AppLogLevel.Information);
}

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public interface IAppLogger
{
    Task LogAsync(AppLogLevel level, string eventName, string message, Exception? exception = null, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default);
}

public sealed class JsonFileLogger(string logDirectory, AppLogLevel minimumLevel) : IAppLogger, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task LogAsync(AppLogLevel level, string eventName, string message, Exception? exception = null, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default)
    {
        if (level < minimumLevel)
        {
            return;
        }

        Directory.CreateDirectory(logDirectory);
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = level.ToString(),
            eventName,
            message,
            exception = exception?.ToString(),
            properties
        };
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(logDirectory, $"vmce-{DateTime.UtcNow:yyyyMMdd}.jsonl"), line, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public void Dispose()
    {
        writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class NullAppLogger : IAppLogger
{
    public Task LogAsync(AppLogLevel level, string eventName, string message, Exception? exception = null, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsService(IFileSystem fileSystem, IInstallationDetector installationDetector, string settingsPath) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!fileSystem.FileExists(settingsPath))
        {
            return AppSettings.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(await fileSystem.ReadAllTextAsync(settingsPath, cancellationToken), JsonOptions) ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
    }

    public async Task<OperationResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(settings.ManualInstallationPath) && !installationDetector.IsValidInstallation(settings.ManualInstallationPath))
        {
            return OperationResult.Failure(ErrorCode.GameNotFound, "The selected directory is not a valid VALORANT installation.");
        }

        try
        {
            fileSystem.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            await fileSystem.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
            return OperationResult.Success("Settings saved.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Failure(ErrorCode.FileSystemError, "Settings could not be saved.", exception.Message);
        }
    }
}

public sealed record DiagnosticReport(
    string ApplicationVersion,
    string OperatingSystem,
    string Architecture,
    DateTimeOffset GeneratedAt,
    GameState Game);

public interface IDiagnosticsService
{
    Task<DiagnosticReport> CreateAsync(string? configuredPath, string payloadManifestPath, CancellationToken cancellationToken = default);
}

public sealed class DiagnosticsService(IStatusService statusService, string applicationVersion) : IDiagnosticsService
{
    public async Task<DiagnosticReport> CreateAsync(string? configuredPath, string payloadManifestPath, CancellationToken cancellationToken = default)
    {
        var state = await statusService.GetAsync(configuredPath, payloadManifestPath, cancellationToken);
        if (state.InstallationPath is not null)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            state = state with { InstallationPath = state.InstallationPath.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) };
        }

        return new DiagnosticReport(applicationVersion, Environment.OSVersion.VersionString, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), DateTimeOffset.UtcNow, state);
    }
}

public sealed record ApplicationServiceSet(
    IStatusService Status,
    IOperationPlanner Planner,
    IOperationExecutor Executor,
    IBackupService Backup,
    IRestoreService Restore,
    ISettingsService Settings,
    IDiagnosticsService Diagnostics,
    IAppLogger Logger,
    string DataDirectory,
    string LogDirectory);

public static class ApplicationServiceFactory
{
    public static ApplicationServiceSet Create(string? dataDirectory = null)
    {
        var appData = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValorantMatureContentManager");
        var backupRoot = Path.Combine(appData, "backups");
        var logRoot = Path.Combine(appData, "logs");
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        IFileSystem fileSystem = new PhysicalFileSystem();
        IHashService hash = new Sha256HashService(fileSystem);
        var installation = new InstallationDetector(fileSystem);
        var process = new GameProcessDetector();
        var payload = new PayloadService(fileSystem, hash);
        var backup = new BackupService(fileSystem, hash, backupRoot);
        var restore = new RestoreService(fileSystem, hash, backup);
        var status = new StatusService(installation, new GameVersionDetector(fileSystem), process, payload, new CompatibilityService(), new StateDetector(fileSystem, hash), backup);
        var settings = new SettingsService(fileSystem, installation, Path.Combine(appData, "settings.json"));
        var logger = new JsonFileLogger(logRoot, AppLogLevel.Information);
        return new ApplicationServiceSet(
            status,
            new OperationPlanner(status, backup, payload),
            new OperationExecutor(fileSystem, hash, backup, restore, process, version),
            backup,
            restore,
            settings,
            new DiagnosticsService(status, version),
            logger,
            appData,
            logRoot);
    }
}