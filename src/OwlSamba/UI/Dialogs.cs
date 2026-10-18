using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OwlSamba.UI;

/// <summary>Dark, theme-matching confirmation dialog.</summary>
public static class Dialogs
{
    public static bool Confirm(string title, string message, string okLabel = "OK", bool danger = false)
    {
        var app = Application.Current;
        var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        Brush R(string key) => (Brush)app.FindResource(key);

        var ok = new Button
        {
            Content = okLabel,
            Style = (Style)app.FindResource(danger ? "DangerButton" : "PrimaryButton"),
            IsDefault = true,
            MinWidth = 110,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var cancel = new Button
        {
            Content = "CANCEL",
            Style = (Style)app.FindResource("SecondaryButton"),
            IsCancel = true,
            MinWidth = 110,
        };

        var icon = new LucideIcon
        {
            Kind = danger ? "TriangleAlert" : "ShieldAlert",
            Size = 22,
            Foreground = danger ? R("DangerBrush") : R("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 14, 0),
        };
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Style = (Style)app.FindResource("H2"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, Style = (Style)app.FindResource("Label"), Margin = new Thickness(0, 8, 0, 0) },
            },
        };
        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Children = { header, buttons } };
        var frame = new Border
        {
            Background = R("PanelBrush"),
            BorderBrush = danger ? R("DangerBrush") : R("AccentBrush"),
            BorderThickness = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(24),
            Child = body,
        };
        var outer = new Border { BorderBrush = R("LineStrongBrush"), BorderThickness = new Thickness(1), Child = frame };

        var window = new Window
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.Height,
            Width = 440,
            ShowInTaskbar = false,
            Content = outer,
            Foreground = R("TextBrush"),
            FontFamily = (FontFamily)app.FindResource("BodyFont"),
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        return window.ShowDialog() == true;
    }
}
