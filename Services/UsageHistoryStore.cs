using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UsageMonitor.Models;

namespace UsageMonitor.Services;

public sealed class UsageHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _retention;
    private DateTimeOffset _lastCompactedAtUtc = DateTimeOffset.MinValue;

    public UsageHistoryStore(string? historyPath = null, TimeSpan? retention = null)
    {
        HistoryPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UsageMonitor",
            "account-usage-history.jsonl");
        _retention = retention ?? TimeSpan.FromDays(90);
    }

    public string HistoryPath { get; }

    public async Task RecordAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Usage is null)
        {
            return;
        }

        var latestBucket = snapshot.Usage.DailyUsageBuckets.Count == 0
            ? null
            : snapshot.Usage.DailyUsageBuckets[^1];
        var sample = new UsageHistorySample(
            snapshot.RefreshedAt.ToUniversalTime(),
            latestBucket?.StartDate,
            snapshot.Usage.TodayTokens,
            snapshot.Usage.LifetimeTokens,
            snapshot.Usage.PeakDailyTokens,
            snapshot.Usage.CurrentStreakDays,
            snapshot.Usage.LongestStreakDays,
            snapshot.Usage.LongestRunningTurnSeconds);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(HistoryPath))!);
            await using (var stream = new FileStream(
                HistoryPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(sample, JsonOptions).AsMemory(),
                    cancellationToken);
            }

            var capturedAtUtc = sample.CapturedAtUtc;
            if (capturedAtUtc - _lastCompactedAtUtc >= TimeSpan.FromDays(1))
            {
                await CompactCoreAsync(capturedAtUtc - _retention, cancellationToken);
                _lastCompactedAtUtc = capturedAtUtc;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<UsageHistorySample>> LoadAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(sinceUtc.ToUniversalTime(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<UsageHistorySample>> LoadCoreAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var samples = new List<UsageHistorySample>();
        if (!File.Exists(HistoryPath))
        {
            return samples;
        }

        using var stream = new FileStream(
            HistoryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDeserialize(line, out var sample) || sample.CapturedAtUtc < sinceUtc)
            {
                continue;
            }

            samples.Add(sample);
        }

        return samples;
    }

    private async Task CompactCoreAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        if (!File.Exists(HistoryPath))
        {
            return;
        }

        var retained = await LoadCoreAsync(cutoffUtc, cancellationToken);
        var temporaryPath = HistoryPath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var sample in retained)
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(sample, JsonOptions).AsMemory(),
                    cancellationToken);
            }
        }

        File.Move(temporaryPath, HistoryPath, overwrite: true);
    }

    private static bool TryDeserialize(string line, out UsageHistorySample sample)
    {
        sample = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UsageHistorySample>(line, JsonOptions);
            if (parsed is null || parsed.CapturedAtUtc == default)
            {
                return false;
            }

            sample = parsed with
            {
                CapturedAtUtc = parsed.CapturedAtUtc.ToUniversalTime(),
                TodayTokens = ClampNonNegative(parsed.TodayTokens),
                LifetimeTokens = ClampNonNegative(parsed.LifetimeTokens),
                PeakDailyTokens = ClampNonNegative(parsed.PeakDailyTokens),
                CurrentStreakDays = ClampNonNegative(parsed.CurrentStreakDays),
                LongestStreakDays = ClampNonNegative(parsed.LongestStreakDays),
                LongestRunningTurnSeconds = ClampNonNegative(parsed.LongestRunningTurnSeconds)
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? ClampNonNegative(long? value) => value is null ? null : Math.Max(0, value.Value);

    private static int? ClampNonNegative(int? value) => value is null ? null : Math.Max(0, value.Value);
}
