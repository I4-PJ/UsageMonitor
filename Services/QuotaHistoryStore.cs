using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UsageMonitor.Models;

namespace UsageMonitor.Services;

public sealed class QuotaHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _retention;
    private DateTimeOffset _lastCompactedAtUtc = DateTimeOffset.MinValue;

    public QuotaHistoryStore(string? historyPath = null, TimeSpan? retention = null)
    {
        HistoryPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UsageMonitor",
            "quota-history.jsonl");
        _retention = retention ?? TimeSpan.FromDays(15);
    }

    public string HistoryPath { get; }

    public async Task RecordAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var capturedAtUtc = snapshot.RefreshedAt.ToUniversalTime();
        var samples = FlattenSnapshot(snapshot, capturedAtUtc);
        if (samples.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            await using (var stream = new FileStream(
                HistoryPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var sample in samples)
                {
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(sample, JsonOptions).AsMemory(),
                        cancellationToken);
                }
            }

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

    public async Task<IReadOnlyList<QuotaHistorySample>> LoadAsync(
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

    private static List<QuotaHistorySample> FlattenSnapshot(
        QuotaSnapshot snapshot,
        DateTimeOffset capturedAtUtc)
    {
        var samples = new List<QuotaHistorySample>();
        foreach (var limit in snapshot.Limits)
        {
            AddWindow(samples, capturedAtUtc, limit, "primary", limit.Primary);
            AddWindow(samples, capturedAtUtc, limit, "secondary", limit.Secondary);
        }

        return samples;
    }

    private static void AddWindow(
        ICollection<QuotaHistorySample> samples,
        DateTimeOffset capturedAtUtc,
        QuotaLimit limit,
        string windowRole,
        QuotaWindow? window)
    {
        if (window is null || window.WindowDurationMinutes <= 0)
        {
            return;
        }

        samples.Add(new QuotaHistorySample(
            capturedAtUtc,
            limit.LimitId,
            limit.DisplayName,
            windowRole,
            window.WindowDurationMinutes,
            window.ResetsAt?.ToUniversalTime(),
            Math.Clamp(window.UsedPercent, 0, 100)));
    }

    private async Task<IReadOnlyList<QuotaHistorySample>> LoadCoreAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var samples = new List<QuotaHistorySample>();
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

    private static bool TryDeserialize(string line, out QuotaHistorySample sample)
    {
        sample = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<QuotaHistorySample>(line, JsonOptions);
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.LimitId) ||
                string.IsNullOrWhiteSpace(parsed.WindowRole) ||
                parsed.WindowDurationMinutes <= 0)
            {
                return false;
            }

            sample = parsed with
            {
                CapturedAtUtc = parsed.CapturedAtUtc.ToUniversalTime(),
                ResetsAtUtc = parsed.ResetsAtUtc?.ToUniversalTime(),
                UsedPercent = Math.Clamp(parsed.UsedPercent, 0, 100)
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
