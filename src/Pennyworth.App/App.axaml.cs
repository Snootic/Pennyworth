using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pennyworth.App.Localization;
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
            Localizer.Instance.UseWording(platform);
            if (OperatingSystem.IsLinux() && platform is LinuxPlatform linux && ReportPreflight(linux.Preflight) is { Count: > 0 } blocking)
            {
                // The scan or the live numbers cannot work here: say what is missing instead of showing broken screens.
                desktop.MainWindow = new StartupProblemWindow(blocking);
                base.OnFrameworkInitializationCompleted();
                return;
            }

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
                Assistant: platform.CreateAssistant(() => monitor?.Sample().Memory.Available ?? 0),
                ExtendsIntoTitleBar: platform.ExtendsIntoTitleBar,
                MemoryModules: platform.MemoryModules,
                Privileged: platform.Privileged));

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                viewModel.Specs.CancelModulesRead(); // Withdraws a pending password request.
                (monitor as IDisposable)?.Dispose();
            };
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

    /// <summary>
    /// Writes each missing native part to standard error as one sentence (a missing keyring library only turns the
    /// keyring off) and returns the localized sentences for the parts the core screens cannot work without.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static List<string> ReportPreflight(PreflightReport report)
    {
        foreach (PreflightProblem problem in report.Problems)
        {
            Console.Error.WriteLine($"Pennyworth: {problem.Message}");
        }

        return
        [
            .. report.Blocking.Select(problem => string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance[problem.Feature == PreflightFeature.Storage ? "StartupProblemStorage" : "StartupProblemMonitoring"],
                problem.Missing)),
        ];
    }

    private static IPlatform CreatePlatform() =>
        OperatingSystem.IsWindows() ? new WindowsPlatform()
        : OperatingSystem.IsLinux() ? new LinuxPlatform()
        : throw new PlatformNotSupportedException("Pennyworth runs on Windows and Linux only.");
}
