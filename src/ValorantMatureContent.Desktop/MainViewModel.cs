using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ValorantMatureContent.Core;

namespace ValorantMatureContent.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ApplicationServiceSet services;
    private readonly string payloadPath;
    private readonly Func<OperationPlan, Task<bool>> confirmPlanAsync;
    private readonly Action<string> copyText;
    private GameState? state;
    private bool isBusy;
    private string manualPath = string.Empty;
    private string details = "Refreshing will inspect the installation.";
    private string progressText = "Ready";

    public MainViewModel(ApplicationServiceSet services, string payloadPath, Func<OperationPlan, Task<bool>> confirmPlanAsync, Action<string> copyText)
    {
        this.services = services;
        this.payloadPath = payloadPath;
        this.confirmPlanAsync = confirmPlanAsync;
        this.copyText = copyText;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        EnableCommand = new AsyncCommand(token => RunAsync(OperationKind.Enable, token), () => CanEnable);
        DisableCommand = new AsyncCommand(token => RunAsync(OperationKind.Disable, token), () => CanDisable);
        VerifyCommand = new AsyncCommand(token => RunAsync(OperationKind.Verify, token), () => state?.InstallationPath is not null && !IsBusy);
        RepairCommand = new AsyncCommand(token => RunAsync(OperationKind.Repair, token), () => state?.InstallationPath is not null && !IsBusy && !state.IsGameRunning);
        BackupCommand = new AsyncCommand(token => RunAsync(OperationKind.Backup, token), () => state?.InstallationPath is not null && !IsBusy && !state.IsGameRunning);
        DiagnoseCommand = new AsyncCommand(DiagnoseAsync, () => !IsBusy);
        CopyReportCommand = new AsyncCommand(CopyReportAsync, () => !IsBusy);
        SavePathCommand = new AsyncCommand(SavePathAsync, () => !IsBusy);
        OpenPaksCommand = new AsyncCommand(token => OpenFolderAsync(state?.InstallationPath is null ? null : Path.Combine(state.InstallationPath, GameFiles.PaksRelativePath), token), () => state?.InstallationPath is not null);
        OpenLogsCommand = new AsyncCommand(token => OpenFolderAsync(services.LogDirectory, token));
        LaunchCommand = new AsyncCommand(LaunchAsync, () => !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand DiagnoseCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand SavePathCommand { get; }
    public ICommand OpenPaksCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand LaunchCommand { get; }

    public string InstallationPath => state?.InstallationPath ?? "Not found";
    public string GameVersion => state?.GameVersion ?? "Unknown";
    public string ContentState => state?.MatureContentState.ToString() ?? "Unknown";
    public string Compatibility => state?.CompatibilityStatus.ToString() ?? "Unknown";
    public string PayloadVersion => state?.PayloadVersion ?? "Unavailable";
    public string Backup => state?.BackupStatus.ToString() ?? "Missing";
    public bool CanEnable => !IsBusy && state is { IsGameRunning: false, MatureContentState: MatureContentState.Disabled, CompatibilityStatus: CompatibilityStatus.Supported };
    public bool CanDisable => !IsBusy && state is { IsGameRunning: false, BackupStatus: BackupStatus.Available } && state.MatureContentState != MatureContentState.Disabled;
    public string ActionAvailability => state?.IsGameRunning == true
        ? "Unavailable: VALORANT or Riot Client must be closed before files can be changed."
        : state?.CompatibilityStatus != CompatibilityStatus.Supported
            ? "Enable unavailable: this game build is unknown or unsupported by the selected payload."
            : string.Join(" ", state?.Problems ?? []);
    public string ManualPath { get => manualPath; set => SetField(ref manualPath, value); }
    public string Details { get => details; private set => SetField(ref details, value); }
    public string ProgressText { get => progressText; private set => SetField(ref progressText, value); }
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    private bool IsBusy
    {
        get => isBusy;
        set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(ProgressVisibility));
                RaiseCommands();
            }
        }
    }

    public async Task InitializeAsync()
    {
        var settings = await services.Settings.LoadAsync();
        ManualPath = settings.ManualInstallationPath ?? string.Empty;
        await RefreshAsync(CancellationToken.None);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await WithProgressAsync("Inspecting installation...", async () =>
        {
            state = await services.Status.GetAsync(NullIfEmpty(ManualPath), payloadPath, cancellationToken);
            Details = state.Problems.Count == 0 ? "All detected managed files are internally consistent." : string.Join(Environment.NewLine, state.Problems);
            RaiseStatus();
        });
    }

    private async Task RunAsync(OperationKind kind, CancellationToken cancellationToken)
    {
        await WithProgressAsync($"Planning {kind.ToString().ToLowerInvariant()}...", async () =>
        {
            var plan = await services.Planner.PlanAsync(kind, NullIfEmpty(ManualPath), payloadPath, cancellationToken);
            if (!plan.IsAllowed)
            {
                Details = string.Join(Environment.NewLine, plan.Blockers);
                return;
            }

            if (!plan.IsNoOp && !await confirmPlanAsync(plan))
            {
                Details = "Operation cancelled. No files were changed.";
                return;
            }

            ProgressText = "Creating backup, applying changes, and verifying...";
            var result = await services.Executor.ExecuteAsync(plan, false, cancellationToken);
            Details = string.Join(Environment.NewLine, new[] { result.Message }.Concat(result.Details));
            await services.Logger.LogAsync(
                result.IsSuccess ? AppLogLevel.Information : AppLogLevel.Error,
                $"desktop.{kind.ToString().ToLowerInvariant()}",
                result.Message,
                properties: new Dictionary<string, string?> { ["installationPath"] = plan.InstallationPath },
                cancellationToken: cancellationToken);
            state = await services.Status.GetAsync(NullIfEmpty(ManualPath), payloadPath, cancellationToken);
            RaiseStatus();
        });
    }

    private async Task DiagnoseAsync(CancellationToken cancellationToken)
    {
        await WithProgressAsync("Building diagnostic report...", async () =>
        {
            var report = await services.Diagnostics.CreateAsync(NullIfEmpty(ManualPath), payloadPath, cancellationToken);
            Details = JsonSerializer.Serialize(report, JsonOptions);
        });
    }

    private async Task CopyReportAsync(CancellationToken cancellationToken)
    {
        var report = await services.Diagnostics.CreateAsync(NullIfEmpty(ManualPath), payloadPath, cancellationToken);
        copyText(JsonSerializer.Serialize(report, JsonOptions));
        Details = "Diagnostic report copied to the clipboard.";
    }

    private async Task SavePathAsync(CancellationToken cancellationToken)
    {
        var settings = await services.Settings.LoadAsync(cancellationToken);
        var result = await services.Settings.SaveAsync(settings with { ManualInstallationPath = NullIfEmpty(ManualPath) }, cancellationToken);
        Details = result.Message;
        if (result.IsSuccess)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    private static Task OpenFolderAsync(string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(path);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        return Task.CompletedTask;
    }

    private static Task LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo("riotclient://launch-product=valorant&launch-patchline=live") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task WithProgressAsync(string message, Func<Task> action)
    {
        IsBusy = true;
        ProgressText = message;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Details = $"Operation failed: {exception.Message}";
            await services.Logger.LogAsync(AppLogLevel.Error, "desktop.error", "Desktop operation failed.", exception);
        }
        finally
        {
            ProgressText = "Ready";
            IsBusy = false;
        }
    }

    private void RaiseStatus()
    {
        foreach (var property in new[] { nameof(InstallationPath), nameof(GameVersion), nameof(ContentState), nameof(Compatibility), nameof(PayloadVersion), nameof(Backup), nameof(CanEnable), nameof(CanDisable), nameof(ActionAvailability) })
        {
            OnPropertyChanged(property);
        }

        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { RefreshCommand, EnableCommand, DisableCommand, VerifyCommand, RepairCommand, BackupCommand, DiagnoseCommand, CopyReportCommand, SavePathCommand, OpenPaksCommand, OpenLogsCommand, LaunchCommand })
        {
            ((AsyncCommand)command).RaiseCanExecuteChanged();
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(CancellationToken.None);
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}