using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ValorantMatureContent.Core;

namespace ValorantMatureContent.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "enable", "disable", "verify", "repair", "backup", "restore", "diagnose", "open-paks"
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0];
        if (!Commands.Contains(command))
        {
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintHelp();
            return 2;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintCommandHelp(command);
            return 0;
        }

        var services = ApplicationServiceFactory.Create();
        var settings = await services.Settings.LoadAsync();
        var configuredPath = GetOption(args, "--path") ?? settings.ManualInstallationPath;
        var payloadPath = ResolvePayloadPath(GetOption(args, "--payload"));
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        try
        {
            return command.ToLowerInvariant() switch
            {
                "status" => await ShowStatusAsync(services, configuredPath, payloadPath, json),
                "diagnose" => await DiagnoseAsync(services, configuredPath, payloadPath, json),
                "open-paks" => await OpenPaksAsync(services, configuredPath, payloadPath),
                _ => await RunOperationAsync(services, ParseOperation(command), configuredPath, payloadPath, dryRun, json)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await services.Logger.LogAsync(AppLogLevel.Error, "cli.unhandled", "CLI command failed.", exception, new Dictionary<string, string?> { ["command"] = command });
            Console.Error.WriteLine($"Unexpected failure: {exception.Message}");
            return 70;
        }
    }

    private static async Task<int> ShowStatusAsync(ApplicationServiceSet services, string? configuredPath, string payloadPath, bool json)
    {
        var state = await services.Status.GetAsync(configuredPath, payloadPath);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
        }
        else
        {
            Console.WriteLine("Valorant Mature Content Manager");
            Console.WriteLine();
            Console.WriteLine($"Installation  {state.InstallationPath ?? "Not found"}");
            Console.WriteLine($"Game version  {state.GameVersion ?? "Unknown"}");
            Console.WriteLine($"Game running  {(state.IsGameRunning ? "Yes" : "No")}");
            Console.WriteLine($"State         {state.MatureContentState}");
            Console.WriteLine($"Payload       {state.PayloadVersion ?? "Unavailable"}");
            Console.WriteLine($"Compatibility {state.CompatibilityStatus}");
            Console.WriteLine($"Backup        {state.BackupStatus}");
            foreach (var problem in state.Problems)
            {
                Console.WriteLine($"Warning       {problem}");
            }
        }

        return state.InstallationPath is null ? 10 : 0;
    }

    private static async Task<int> RunOperationAsync(ApplicationServiceSet services, OperationKind kind, string? configuredPath, string payloadPath, bool dryRun, bool json)
    {
        var plan = await services.Planner.PlanAsync(kind, configuredPath, payloadPath);
        if (dryRun)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
            }
            else
            {
                Console.WriteLine("No files will be changed.");
                Console.WriteLine(plan.Summary);
                foreach (var action in plan.Actions)
                {
                    Console.WriteLine($"{action.Kind,-14} {action.Path}");
                }

                foreach (var blocker in plan.Blockers)
                {
                    Console.WriteLine($"Blocked: {blocker}");
                }
            }
        }

        var result = await services.Executor.ExecuteAsync(plan, dryRun);
        await services.Logger.LogAsync(
            result.IsSuccess ? AppLogLevel.Information : AppLogLevel.Error,
            $"operation.{kind.ToString().ToLowerInvariant()}",
            result.Message,
            properties: new Dictionary<string, string?>
            {
                ["installationPath"] = plan.InstallationPath,
                ["gameVersion"] = plan.GameVersion,
                ["dryRun"] = dryRun.ToString()
            });

        if (!dryRun)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                Console.WriteLine(result.Message);
                foreach (var detail in result.Details)
                {
                    Console.WriteLine(detail);
                }
            }
        }

        return result.IsSuccess ? 0 : MapExitCode(result.ErrorCode);
    }

    private static async Task<int> DiagnoseAsync(ApplicationServiceSet services, string? configuredPath, string payloadPath, bool json)
    {
        var report = await services.Diagnostics.CreateAsync(configuredPath, payloadPath);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            Console.WriteLine($"Application {report.ApplicationVersion}");
            Console.WriteLine($"OS          {report.OperatingSystem} ({report.Architecture})");
            Console.WriteLine($"Generated   {report.GeneratedAt:O}");
            Console.WriteLine($"Install     {report.Game.InstallationPath ?? "Not found"}");
            Console.WriteLine($"Build       {report.Game.GameVersion ?? "Unknown"}");
            Console.WriteLine($"State       {report.Game.MatureContentState}");
            Console.WriteLine($"Compatible  {report.Game.CompatibilityStatus}");
            foreach (var file in report.Game.Files)
            {
                Console.WriteLine($"File        {file.Name}: exists={file.Exists}, size={file.Size?.ToString(CultureInfo.InvariantCulture) ?? "-"}, valid={file.IsValid?.ToString() ?? "unknown"}");
            }
        }

        return report.Game.InstallationPath is null ? 10 : 0;
    }

    private static async Task<int> OpenPaksAsync(ApplicationServiceSet services, string? configuredPath, string payloadPath)
    {
        var state = await services.Status.GetAsync(configuredPath, payloadPath);
        if (state.InstallationPath is null)
        {
            Console.Error.WriteLine("A valid VALORANT installation was not found.");
            return 10;
        }

        var path = Path.Combine(state.InstallationPath, GameFiles.PaksRelativePath);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return 0;
    }

    private static OperationKind ParseOperation(string command) => command.ToLowerInvariant() switch
    {
        "enable" => OperationKind.Enable,
        "disable" => OperationKind.Disable,
        "verify" => OperationKind.Verify,
        "repair" => OperationKind.Repair,
        "backup" => OperationKind.Backup,
        "restore" => OperationKind.Restore,
        _ => throw new InvalidOperationException($"Unsupported operation: {command}")
    };

    private static int MapExitCode(ErrorCode code) => code switch
    {
        ErrorCode.GameNotFound => 10,
        ErrorCode.GameRunning => 11,
        ErrorCode.UnsupportedGameVersion => 12,
        ErrorCode.PayloadHashMismatch or ErrorCode.InvalidManifest => 13,
        ErrorCode.BackupFailed => 14,
        ErrorCode.RollbackFailed => 16,
        ErrorCode.InvalidState => 17,
        _ => 1
    };

    private static string? GetOption(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ResolvePayloadPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "Content", "payload.manifest.json");
        return File.Exists(packaged) ? packaged : Path.GetFullPath(Path.Combine("Content", "payload.manifest.json"));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Valorant Mature Content Manager");
        Console.WriteLine("Usage: vmce <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands: status, enable, disable, verify, repair, backup, restore, diagnose, open-paks");
        Console.WriteLine("Options:  --path <install>  --payload <manifest>  --json  --dry-run  --help");
    }

    private static void PrintCommandHelp(string command)
    {
        Console.WriteLine($"Usage: vmce {command} [--path <install>] [--payload <manifest>]" +
            (command is "enable" or "disable" or "repair" ? " [--dry-run]" : string.Empty) + " [--json]");
    }
}
