using System.Windows;
using System.Windows.Threading;
using OwlSamba.Core;
using OwlSamba.Platform;
using OwlSamba.UI.ViewModels;
using Forms = System.Windows.Forms;

namespace OwlSamba.UI;

public partial class App : Application
{
    private const string MutexName = @"Local\OwlSamba.UI";
    private const string ShowEventName = @"Local\OwlSamba.UI.Show";

    private bool _startHidden;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private bool _exiting;

    public static int RunDesktop(bool startHidden)
    {
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask the existing instance to come to the front.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            return 0;
        }
        var app = new App { _startHidden = startHidden };
        app.InitializeComponent();
        return app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            _shell?.Toast("Unexpected error: " + args.Exception.Message, "error");
            args.Handled = true;
        };

        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);

        _shell = new ShellViewModel(Dispatcher);
        _window = new MainWindow { DataContext = _shell };
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            if (_shell.Prefs.MinimizeToTray && ServiceInstaller.IsInstalled())
            {
                args.Cancel = true;
                _window.Hide();
                ShowBalloon("OwlSamba is still protecting you", "The service keeps running. The app is now in the system tray.", Forms.ToolTipIcon.Info);
            }
            else
            {
                ExitApp();
            }
        };
        _shell.Notification += OnNotification;
        CreateTray();

        if (!_startHidden) _window.Show();
        await _shell.StartAsync();
    }

    private void ShowMain()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/owlsamba.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add("Open OwlSamba", null, (_, _) => ShowMain());
        menu.Items.Add("Live activity", null, (_, _) => { ShowMain(); _shell?.NavigateCommand.Execute("activity"); });
        menu.Items.Add("Bans", null, (_, _) => { ShowMain(); _shell?.NavigateCommand.Execute("bans"); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Close the app (service keeps running)", null, (_, _) => ExitApp());
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF3);
            item.Padding = new Forms.Padding(4, 5, 4, 5);
        }

        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "OwlSamba — SMB protection",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }

    private void OnNotification(LiveEvent e)
    {
        if (_shell?.Prefs.BanNotifications != true || _window == null) return;
        if (_window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized) return;
        if (e.Type == "ban" && e.Ban != null)
            ShowBalloon(e.Ban.ExpiresAt == null ? $"⛔ {e.Ip} banned permanently" : $"⏱ {e.Ip} blocked for {Fmt.Remaining(e.Ban.ExpiresAt)}",
                e.Ban.Reason ?? "", Forms.ToolTipIcon.Warning);
        else if (e.Type == "alert")
            ShowBalloon("🚨 Security alert", e.Message ?? "", Forms.ToolTipIcon.Error);
    }

    private void ShowBalloon(string title, string text, Forms.ToolTipIcon icon)
    {
        if (_tray == null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(5000);
    }

    private void ExitApp()
    {
        _exiting = true;
        _shell?.Shutdown();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _window?.Close();
        Shutdown();
    }

    /// <summary>Corsair-like dark context menu for the tray icon.</summary>
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        private sealed class DarkColors : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x16, 0x16, 0x1A);
            private static readonly System.Drawing.Color Sel = System.Drawing.Color.FromArgb(0x25, 0x25, 0x2B);
            private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x42);
            private static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(0xFF, 0xD4, 0x00);
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Accent;
            public override System.Drawing.Color MenuItemSelected => Sel;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Bg;
        }
    }
}
