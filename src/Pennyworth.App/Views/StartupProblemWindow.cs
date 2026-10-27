using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pennyworth.App.Localization;

namespace Pennyworth.App.Views;

/// <summary>
/// What replaces the main window when the platform lacks a part the core screens cannot work without: the part's
/// name in plain words and what to do, nothing else. Built in code because it must not depend on anything the
/// failed start-up would have created.
/// </summary>
public sealed class StartupProblemWindow : Window
{
    public StartupProblemWindow(IReadOnlyList<string> messages)
    {
        Localizer text = Localizer.Instance;
        Title = text["StartupProblemTitle"];
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var close = new Button
        {
            Content = text["StartupProblemClose"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        close.Click += (_, _) => Close();

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (string message in messages)
        {
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(new TextBlock { Text = text["StartupProblemHint"], TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        panel.Children.Add(close);
        Content = panel;
    }
}
