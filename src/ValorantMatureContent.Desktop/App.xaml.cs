using System.IO;
using System.Windows;
using ValorantMatureContent.Core;

namespace ValorantMatureContent.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = ApplicationServiceFactory.Create();
        var payloadPath = Path.Combine(AppContext.BaseDirectory, "Content", "payload.manifest.json");
        var viewModel = new MainViewModel(services, payloadPath, ConfirmPlanAsync, text => Clipboard.SetText(text));
        new MainWindow(viewModel).Show();
    }

    private static Task<bool> ConfirmPlanAsync(OperationPlan plan)
    {
        var actions = string.Join(Environment.NewLine, plan.Actions.Select(action => $"{action.Kind}: {action.Path}"));
        var result = MessageBox.Show(
            $"{plan.Summary}{Environment.NewLine}{Environment.NewLine}{actions}",
            $"Confirm {plan.Kind}",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.OK);
    }
}

