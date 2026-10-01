using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UsageMonitor.Models;
using UsageMonitor.Services;

namespace UsageMonitor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICodexQuotaClient _quotaClient;
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _settingsStore;
    private readonly QuotaHistoryStore _historyStore;
    private readonly QuotaHistoryAnalyzer _historyAnalyzer;

    public event Action<QuotaSnapshot>? QuotaRefreshed;

    public event Action? QuotaRefreshFailed;

    public event Action<int>? RefreshIntervalChanged;

    public QuotaSnapshot? LastSnapshot { get; private set; }

    public IReadOnlyList<RefreshIntervalOption> RefreshIntervalOptions { get; } =
    [
        new(1, "1 minute"),
        new(5, "5 minutes"),
        new(15, "15 minutes"),
        new(30, "30 minutes")
    ];

    [ObservableProperty]
    private string _statusText = "Waiting for first refresh";

    [ObservableProperty]
    private string _statusColor = "#8E9BAC";

    [ObservableProperty]
    private string _lastUpdatedText = "Not updated yet";

    [ObservableProperty]
    private bool _hasRefreshError;

    [ObservableProperty]
    private string _refreshErrorText = string.Empty;

    [ObservableProperty]
    private bool _hasQuota;

    [ObservableProperty]
    private string _historyStatusText = "Quota history will begin with the first successful refresh.";

    [ObservableProperty]
    private bool _hasHistoryCharts;

    [ObservableProperty]
    private string _headlineForecastText = "Learning usage pattern";

    [ObservableProperty]
    private string _headlineForecastTitle = "Primary Codex window";

    [ObservableProperty]
    private string _headlineForecastDetail = "Collecting quota history for the primary Codex window.";

    [ObservableProperty]
    private RefreshIntervalOption? _selectedRefreshIntervalOption;

    public ObservableCollection<QuotaLimitViewModel> Limits { get; } = [];

    public ObservableCollection<QuotaHistoryChartViewModel> HistoryCharts { get; } = [];

    public MainWindowViewModel()
        : this(new AppSettingsStore())
    {
    }

    public MainWindowViewModel(AppSettingsStore settingsStore)
        : this(settingsStore, settingsStore.Load())
    {
    }

    public MainWindowViewModel(AppSettingsStore settingsStore, AppSettings settings)
        : this(settingsStore, settings, new QuotaHistoryStore(), new QuotaHistoryAnalyzer())
    {
    }

    public MainWindowViewModel(
        AppSettingsStore settingsStore,
        AppSettings settings,
        QuotaHistoryStore historyStore,
        QuotaHistoryAnalyzer historyAnalyzer)
        : this(settingsStore, settings, historyStore, historyAnalyzer, new CodexQuotaClient())
    {
    }

    internal MainWindowViewModel(
        AppSettingsStore settingsStore,
        AppSettings settings,
        QuotaHistoryStore historyStore,
        QuotaHistoryAnalyzer historyAnalyzer,
        ICodexQuotaClient quotaClient)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _historyStore = historyStore;
        _historyAnalyzer = historyAnalyzer;
        _quotaClient = quotaClient;
        SelectedRefreshIntervalOption = RefreshIntervalOptions.FirstOrDefault(option => option.Minutes == settings.RefreshIntervalMinutes)
            ?? RefreshIntervalOptions.First(option => option.Minutes == 5);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            HasRefreshError = false;
            StatusText = "Refreshing…";
            StatusColor = "#E9BD70";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var snapshot = await _quotaClient.ReadQuotaAsync(timeout.Token);
            LastSnapshot = snapshot;

            Limits.Clear();
            foreach (var limit in snapshot.Limits.Select(limit => new QuotaLimitViewModel(limit)))
            {
                Limits.Add(limit);
            }

            HasQuota = Limits.Count > 0;
            LastUpdatedText = $"Updated {snapshot.RefreshedAt:HH:mm:ss}";
            await UpdateHistoryAsync(snapshot);
            StatusText = "Live";
            StatusColor = "#78E0BE";
            QuotaRefreshed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            HasQuota = Limits.Count > 0;
            HasRefreshError = true;
            RefreshErrorText = BuildRefreshErrorText(ex);
            StatusText = "Couldn’t refresh";
            StatusColor = "#FF8A84";
            QuotaRefreshFailed?.Invoke();
        }
    }

    private async Task UpdateHistoryAsync(QuotaSnapshot snapshot)
    {
        try
        {
            await _historyStore.RecordAsync(snapshot);
        }
        catch (Exception)
        {
            HistoryStatusText = "Quota loaded, but saved history could not be updated.";
            return;
        }

        await LoadHistoryAsync(snapshot.RefreshedAt);
    }

    public async Task LoadHistoryAsync(DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        try
        {
            var history = await _historyStore.LoadAsync(
                effectiveNow.ToUniversalTime() - TimeSpan.FromDays(15));
            var charts = _historyAnalyzer.BuildCharts(history, effectiveNow);

            HistoryCharts.Clear();
            foreach (var chart in charts)
            {
                HistoryCharts.Add(chart);
            }

            HasHistoryCharts = HistoryCharts.Count > 0;
            UpdateHeadlineForecast(charts);
            var sampleCount = history.Count;
            HistoryStatusText = sampleCount switch
            {
                0 => "Quota history will begin with the first successful refresh.",
                1 => "1 quota sample recorded. Forecast confidence improves as history accumulates.",
                _ => $"{sampleCount:N0} quota samples recorded over the last 15 days."
            };
        }
        catch (Exception)
        {
            HistoryStatusText = "Saved quota history could not be loaded.";
        }
    }

    private void UpdateHeadlineForecast(IReadOnlyList<QuotaHistoryChartViewModel> charts)
    {
        var primaryCodex = charts.FirstOrDefault(chart =>
                chart.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                && chart.WindowRole.Equals("primary", StringComparison.OrdinalIgnoreCase))
            ?? charts.FirstOrDefault(chart => chart.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase))
            ?? charts.FirstOrDefault();

        if (primaryCodex is null)
        {
            HeadlineForecastTitle = "Primary Codex window";
            HeadlineForecastText = "Learning usage pattern";
            HeadlineForecastDetail = "Collecting quota history for the primary Codex window.";
            return;
        }

        HeadlineForecastTitle = primaryCodex.Title;
        HeadlineForecastText = primaryCodex.ForecastText;
        HeadlineForecastDetail = $"{primaryCodex.CurrentStatus} · {primaryCodex.ForecastDetail}";
    }

    private static string BuildRefreshErrorText(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return "The Codex service did not respond within 45 seconds.";
        }

        var message = exception.GetBaseException().Message.Trim();
        if (message.Contains("Missing optional dependency", StringComparison.OrdinalIgnoreCase))
        {
            return "The installed Codex command is incomplete. Update or reinstall ChatGPT/Codex, then refresh.";
        }

        if (message.Contains("before response", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("closed stdout", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex stopped before returning quota data. Reopen ChatGPT/Codex and try again.";
        }

        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Codex quota data is temporarily unavailable.";
        }

        const int maxLength = 220;
        return firstLine.Length <= maxLength
            ? firstLine
            : $"{firstLine[..maxLength]}…";
    }

    partial void OnSelectedRefreshIntervalOptionChanged(RefreshIntervalOption? value)
    {
        if (value is null || _settings.RefreshIntervalMinutes == value.Minutes)
        {
            return;
        }

        _settings.RefreshIntervalMinutes = value.Minutes;
        _settingsStore.Save(_settings);
        RefreshIntervalChanged?.Invoke(value.Minutes);
    }
}
