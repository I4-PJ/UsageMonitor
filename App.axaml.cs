using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using UsageMonitor.Models;
using UsageMonitor.Services;
using UsageMonitor.ViewModels;
using UsageMonitor.Views;

namespace UsageMonitor
{
    public partial class App : Application
    {
        private IClassicDesktopStyleApplicationLifetime? _desktop;
        private MainWindow? _detailWindow;
        private TrayIcon? _trayIcon;
        private MainWindowViewModel? _viewModel;
        private CancellationTokenSource? _refreshLoopCancellation;
        private AppSettingsStore? _settingsStore;
        private bool _isShuttingDown;
        private bool _isShowingDetailWindow;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Line below is needed to remove Avalonia data validation.
                // Without this line you will get duplicate validations from both Avalonia and CT.
                BindingPlugins.DataValidators.RemoveAt(0);

                _desktop = desktop;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _settingsStore = new AppSettingsStore();
                var settings = _settingsStore.Load();
                _viewModel = new MainWindowViewModel(_settingsStore, settings);
                _viewModel.QuotaRefreshed += UpdateTrayStatus;
                _viewModel.QuotaRefreshFailed += UpdateTrayUnknown;
                _viewModel.RefreshIntervalChanged += UpdateRefreshInterval;
                CreateTrayIcon();
                StartRefreshLoop(settings.RefreshIntervalMinutes);
                RefreshQuota();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void RequestShowDetailWindow()
        {
            Dispatcher.UIThread.Post(ShowDetailWindow);
        }

        private void CreateTrayIcon()
        {
            var showItem = new NativeMenuItem("Show Details");
            showItem.Click += (_, _) => RequestShowDetailWindow();

            var refreshItem = new NativeMenuItem("Refresh Now");
            refreshItem.Click += (_, _) => RefreshQuota();

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) => Shutdown();

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            _trayIcon = new TrayIcon
            {
                Icon = LoadTrayIcon("quota-gray.ico"),
                ToolTipText = "Codex Usage Monitor\nQuota not loaded yet.",
                Menu = menu,
                IsVisible = true
            };

            _trayIcon.Clicked += (_, _) => RequestShowDetailWindow();
        }

        private void StartRefreshLoop(int refreshIntervalMinutes)
        {
            _refreshLoopCancellation?.Cancel();
            _refreshLoopCancellation?.Dispose();
            _refreshLoopCancellation = new CancellationTokenSource();
            _ = RunRefreshLoopAsync(refreshIntervalMinutes, _refreshLoopCancellation.Token);
        }

        private void UpdateRefreshInterval(int refreshIntervalMinutes)
        {
            StartRefreshLoop(refreshIntervalMinutes);
        }

        private async Task RunRefreshLoopAsync(int refreshIntervalMinutes, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(refreshIntervalMinutes), cancellationToken);
                    await Dispatcher.UIThread.InvokeAsync(RefreshQuota);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ShowDetailWindow()
        {
            if (_isShowingDetailWindow)
            {
                return;
            }

            _isShowingDetailWindow = true;
            try
            {
                if (_detailWindow is null)
                {
                    _detailWindow = new MainWindow
                    {
                        DataContext = _viewModel
                    };
                    _detailWindow.Closed += (_, _) =>
                    {
                        if (_isShuttingDown)
                        {
                            _detailWindow = null;
                        }
                    };
                    _detailWindow.Closing += (_, args) =>
                    {
                        if (_isShuttingDown)
                        {
                            return;
                        }

                        args.Cancel = true;
                        _detailWindow.Hide();
                    };
                }

                _desktop!.MainWindow = _detailWindow;
                if (!_detailWindow.IsVisible)
                {
                    _detailWindow.Show();
                }

                if (_detailWindow.WindowState == WindowState.Minimized)
                {
                    _detailWindow.WindowState = WindowState.Normal;
                }

                _detailWindow.Activate();
                RefreshQuota();
            }
            finally
            {
                _isShowingDetailWindow = false;
            }
        }

        private void RefreshQuota()
        {
            if (_viewModel?.RefreshCommand.CanExecute(null) == true)
            {
                _viewModel.RefreshCommand.Execute(null);
            }
        }

        private void UpdateTrayStatus(QuotaSnapshot snapshot)
        {
            if (_trayIcon is null)
            {
                return;
            }

            var codex = snapshot.Limits.FirstOrDefault(limit => limit.LimitId == "codex");
            var primary = codex?.Primary;
            _trayIcon.Icon = LoadTrayIcon(GetStatusIconName(primary?.LeftPercent));
            _trayIcon.ToolTipText = BuildTooltip(codex, snapshot.RefreshedAt);
        }

        private void UpdateTrayUnknown()
        {
            if (_trayIcon is null)
            {
                return;
            }

            _trayIcon.Icon = LoadTrayIcon("quota-gray.ico");
            _trayIcon.ToolTipText = "Codex Usage Monitor\nRefresh failed.";
        }

        private static WindowIcon LoadTrayIcon(string fileName)
        {
            using var iconStream = AssetLoader.Open(new Uri($"avares://UsageMonitor/Assets/Tray/{fileName}"));
            return new WindowIcon(iconStream);
        }

        private static string GetStatusIconName(int? leftPercent)
        {
            return leftPercent switch
            {
                null => "quota-gray.ico",
                >= 60 => "quota-green.ico",
                >= 30 => "quota-yellow.ico",
                >= 10 => "quota-orange.ico",
                _ => "quota-red.ico"
            };
        }

        private static string BuildTooltip(QuotaLimit? codex, DateTimeOffset refreshedAt)
        {
            if (codex is null)
            {
                return "Codex Usage Monitor\nCodex quota unavailable.";
            }

            return string.Join(Environment.NewLine,
                "Codex Usage",
                BuildWindowLine("5h", codex.Primary),
                BuildWindowLine("7d", codex.Secondary),
                $"Updated {refreshedAt.ToLocalTime():HH:mm:ss}");
        }

        private static string BuildWindowLine(string label, QuotaWindow? window)
        {
            if (window is null)
            {
                return $"{label} [----------] unknown";
            }

            return $"{label} {BuildBar(window.LeftPercent)} {window.LeftPercent}% left, {FormatReset(window.ResetsAt)}";
        }

        private static string BuildBar(int leftPercent)
        {
            var filled = Math.Clamp((int)Math.Round(leftPercent / 10.0), 0, 10);
            return $"[{new string('#', filled)}{new string('-', 10 - filled)}]";
        }

        private static string FormatReset(DateTimeOffset? resetsAt)
        {
            if (resetsAt is null)
            {
                return "reset unknown";
            }

            var local = resetsAt.Value.ToLocalTime();
            var now = DateTimeOffset.Now;
            if (local.Date == now.Date)
            {
                return $"resets {local:HH:mm}";
            }

            return $"resets {local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
        }

        private void Shutdown()
        {
            _isShuttingDown = true;
            _refreshLoopCancellation?.Cancel();
            _refreshLoopCancellation?.Dispose();
            _trayIcon?.Dispose();
            _desktop?.Shutdown();
        }
    }
}
