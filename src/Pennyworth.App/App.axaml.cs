using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pennyworth.App.Services;
using Pennyworth.App.Services.Windows;
using Pennyworth.App.ViewModels;
using Pennyworth.App.Views;
using Pennyworth.Cleanup;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Platform;
using Pennyworth.Platform.Linux;

namespace Pennyworth.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: the only place concrete services are created, and the only place that
            // asks which operating system this is.
            IPlatform platform = CreatePlatform();
            var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded(platform.Name).Locations, platform.ResolveVariable);
            ISystemMonitor? monitor = platform.CreateMonitor(KnowledgeBaseLoader.LoadProcesses(platform.Name).Processes);
            var viewModel = new MainWindowViewModel(new AppServices(
                platform.Drives,
                platform.Scanner,
                classifier,
                new KnowledgeBaseLocationInfo(classifier, platform.AppNames),
                platform.Explorer,
                platform.CreateCleanup(KnowledgeBaseLoader.LoadCleanupRules(platform.Name).Rules),
                platform.HiddenSpace,
                new FileSettingsStore(platform.SettingsPath),
                platform.Specs,
                monitor,
                platform.Closer,
                new CleanupHistory(platform.CleanupHistoryPath),
                Clipboard: new AvaloniaClipboardService(() => desktop.MainWindow),
                Assistant: platform.CreateAssistant(() => monitor?.Sample().Memory.Available ?? 0)));

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => (monitor as IDisposable)?.Dispose();
            viewModel.Initialize();

            // Live numbers once per second; the first sample right away.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                try
                {
                    await viewModel.TickAsync();
                }
                catch (Exception)
                {
                    // A timer tick is an async void boundary: letting anything escape it terminates
                    // the window. TickAsync already keeps the last numbers when sampling fails.
                }
            };
            timer.Start();
            _ = viewModel.TickAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IPlatform CreatePlatform() =>
        OperatingSystem.IsWindows() ? new WindowsPlatform()
        : OperatingSystem.IsLinux() ? new LinuxPlatform()
        : throw new PlatformNotSupportedException("Pennyworth runs on Windows and Linux only.");
}
