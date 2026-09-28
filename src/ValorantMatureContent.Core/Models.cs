namespace ValorantMatureContent.Core;

public enum MatureContentState
{
    Unknown,
    Disabled,
    Enabled,
    Conflict,
    Corrupted,
    Partial,
    Unsupported
}

public enum CompatibilityStatus
{
    Unknown,
    Supported,
    Unsupported
}

public enum BackupStatus
{
    Missing,
    Available,
    Invalid
}

public enum ErrorCode
{
    None,
    GameNotFound,
    GameRunning,
    UnsupportedGameVersion,
    PayloadHashMismatch,
    BackupFailed,
    InstallFailed,
    RollbackFailed,
    InvalidState,
    InvalidManifest,
    RestoreFailed,
    FileSystemError
}

public sealed record OperationResult(bool IsSuccess, ErrorCode ErrorCode, string Message, IReadOnlyList<string> Details)
{
    public static OperationResult Success(string message) => new(true, ErrorCode.None, message, []);

    public static OperationResult Failure(ErrorCode code, string message, params string[] details) =>
        new(false, code, message, details);

    public static OperationResult<T> Success<T>(T value, string message = "") => new(true, value, ErrorCode.None, message, []);

    public static OperationResult<T> Failure<T>(ErrorCode code, string message, params string[] details) =>
        new(false, default, code, message, details);
}

public sealed record OperationResult<T>(bool IsSuccess, T? Value, ErrorCode ErrorCode, string Message, IReadOnlyList<string> Details);

public sealed record GameState(
    string? InstallationPath,
    string? GameVersion,
    MatureContentState MatureContentState,
    CompatibilityStatus CompatibilityStatus,
    string? PayloadVersion,
    BackupStatus BackupStatus,
    bool IsGameRunning,
    IReadOnlyList<string> Problems,
    IReadOnlyList<FileState> Files);

public sealed record FileState(string Name, bool Exists, long? Size, string? Sha256, bool? IsValid);

public static class GameFiles
{
    public const string PaksRelativePath = "live/ShooterGame/Content/Paks";
    public const string GameExecutableRelativePath = "live/ShooterGame/Binaries/Win64/VALORANT-Win64-Shipping.exe";
    public static readonly string[] Extensions = [".pak", ".sig", ".ucas", ".utoc"];
    public static readonly string[] Original = Extensions.Select(extension => $"VNGLogo-WindowsClient{extension}").ToArray();
    public static readonly string[] Mature = Extensions.Select(extension => $"MatureData-WindowsClient{extension}").ToArray();
}