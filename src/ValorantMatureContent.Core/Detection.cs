using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ValorantMatureContent.Core;

public interface IInstallationDetector
{
    Task<OperationResult<string>> DetectAsync(string? configuredPath = null, CancellationToken cancellationToken = default);
    bool IsValidInstallation(string path);
}

public sealed partial class InstallationDetector(IFileSystem fileSystem) : IInstallationDetector
{
    public async Task<OperationResult<string>> DetectAsync(string? configuredPath = null, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in await GetCandidatesAsync(configuredPath, cancellationToken))
        {
            if (IsValidInstallation(candidate))
            {
                return OperationResult.Success(Path.GetFullPath(candidate), "VALORANT installation detected.");
            }
        }

        return OperationResult.Failure<string>(ErrorCode.GameNotFound, "A valid VALORANT installation was not found.");
    }

    public bool IsValidInstallation(string path) =>
        !string.IsNullOrWhiteSpace(path) && fileSystem.DirectoryExists(Path.Combine(path, GameFiles.PaksRelativePath));

    private async Task<IReadOnlyList<string>> GetCandidatesAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, configuredPath);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var metadata = Path.Combine(programData, "Riot Games", "Metadata", "valorant.live", "valorant.live.product_settings.yaml");
        if (fileSystem.FileExists(metadata))
        {
            var yaml = await fileSystem.ReadAllTextAsync(metadata, cancellationToken);
            var match = InstallPathPattern().Match(yaml);
            if (match.Success)
            {
                AddCandidate(candidates, match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            AddRegistryCandidates(candidates, Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
            AddRegistryCandidates(candidates, Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"));
        }

        foreach (var root in DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.RootDirectory.FullName))
        {
            AddCandidate(candidates, Path.Combine(root, "Riot Games", "VALORANT"));
            AddCandidate(candidates, Path.Combine(root, "Games", "Riot Games", "VALORANT"));
        }

        return candidates;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(List<string> candidates, RegistryKey? uninstallKey)
    {
        using (uninstallKey)
        {
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey is null)
                {
                    continue;
                }

                var displayName = subKey.GetValue("DisplayName") as string;
                if (displayName?.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) == true)
                {
                    AddCandidate(candidates, subKey.GetValue("InstallLocation") as string);
                }
            }
        }
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(path.Trim().Trim('"'));
        }
    }

    [GeneratedRegex("product_install_full_path:\\s*[\\\"']?([^\\r\\n\\\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallPathPattern();
}

public interface IGameVersionDetector
{
    Task<string?> DetectAsync(string installationPath, CancellationToken cancellationToken = default);
}

public sealed class GameVersionDetector(IFileSystem fileSystem) : IGameVersionDetector
{
    public Task<string?> DetectAsync(string installationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(installationPath, GameFiles.GameExecutableRelativePath);
        if (!fileSystem.FileExists(executable))
        {
            return Task.FromResult<string?>(null);
        }

        var version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        return Task.FromResult(string.IsNullOrWhiteSpace(version) ? null : version);
    }
}

public interface IGameProcessDetector
{
    bool IsRunning();
}

public sealed class GameProcessDetector : IGameProcessDetector
{
    private static readonly string[] ProcessNames = ["VALORANT", "VALORANT-Win64-Shipping", "RiotClientServices"];
    public bool IsRunning() => ProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);
}