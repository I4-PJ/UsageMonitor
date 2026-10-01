using UsageMonitor.Models;
using UsageMonitor.Services;
using UsageMonitor.ViewModels;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"UsageMonitor.MainWindowViewModelTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task RefreshFailure_PreservesLastGoodQuotaAndShowsFriendlyError()
    {
        var refreshedAt = new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.FromHours(2));
        var snapshot = new QuotaSnapshot(
            refreshedAt,
            [
                new QuotaLimit(
                    "codex",
                    "Codex",
                    "pro",
                    new QuotaWindow(32, 68, 10_080, refreshedAt.AddDays(2)),
                    null,
                    false,
                    false,
                    false)
            ],
            2);
        var quotaClient = new SequenceQuotaClient(
            _ => Task.FromResult(snapshot),
            _ => Task.FromException<QuotaSnapshot>(
                new InvalidOperationException("Missing optional dependency @openai/codex-darwin-arm64")));
        var viewModel = CreateViewModel(quotaClient);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        var lastUpdatedText = viewModel.LastUpdatedText;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasQuota);
        Assert.Single(viewModel.Limits);
        Assert.True(viewModel.HasRefreshError);
        Assert.Equal("Couldn’t refresh", viewModel.StatusText);
        Assert.Equal("#FF8A84", viewModel.StatusColor);
        Assert.Equal(lastUpdatedText, viewModel.LastUpdatedText);
        Assert.Equal(
            "The installed Codex command is incomplete. Update or reinstall ChatGPT/Codex, then refresh.",
            viewModel.RefreshErrorText);
    }

    private MainWindowViewModel CreateViewModel(ICodexQuotaClient quotaClient)
    {
        Directory.CreateDirectory(_directory);
        return new MainWindowViewModel(
            new AppSettingsStore(),
            new AppSettings(),
            new QuotaHistoryStore(Path.Combine(_directory, "history.jsonl")),
            new QuotaHistoryAnalyzer(),
            quotaClient);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class SequenceQuotaClient : ICodexQuotaClient
    {
        private readonly Queue<Func<CancellationToken, Task<QuotaSnapshot>>> _responses;

        public SequenceQuotaClient(params Func<CancellationToken, Task<QuotaSnapshot>>[] responses)
        {
            _responses = new Queue<Func<CancellationToken, Task<QuotaSnapshot>>>(responses);
        }

        public Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken)
        {
            return _responses.Dequeue()(cancellationToken);
        }
    }
}
