using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OwlSamba.UI.ViewModels;

namespace OwlSamba.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
                shell.PropertyChanged += OnShellChanged;
        };
    }

    /// <summary>Keeps the sidebar in sync when navigation happens from code (tray menu, links).</summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentPage) || sender is not ShellViewModel shell) return;
        RadioButton? target = shell.CurrentPage.Key switch
        {
            "dashboard" => NavDashboard,
            "activity" => NavActivity,
            "ips" => NavIps,
            "bans" => NavBans,
            "whitelist" => NavWhitelist,
            "audit" => NavAudit,
            "settings" => NavSettings,
            _ => null,
        };
        if (target != null) target.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && DataContext is ShellViewModel shell)
            shell.NavigateCommand.Execute(key);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
