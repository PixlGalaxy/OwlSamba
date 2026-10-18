using System.Windows;
using System.Windows.Controls;
using OwlSamba.UI.ViewModels;

namespace OwlSamba.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // PasswordBox can't be data-bound (by design), so it's handed to the view model here.
    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        await vm.SetPasswordAsync(Pw1.Password, Pw2.Password);
        Pw1.Clear();
        Pw2.Clear();
    }
}
