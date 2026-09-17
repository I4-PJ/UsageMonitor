using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UsageMonitor.Models;

namespace UsageMonitor.Services;

public sealed class CodexQuotaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken)
    {
        using var process = StartAppServer();

        var reader = process.StandardOutput;
        var writer = process.StandardInput;
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        await SendRequestAsync(writer, 1, "initialize", new
        {
            clientInfo = new
            {
                name = "usage_monitor",
                title = "Usage Monitor",
                version = "0.1.0"
            },
            capabilities = new
            {
                experimentalApi = true
            }
        }, cancellationToken);

        await ReadResponseAsync(reader, process, stderrTask, 1, cancellationToken);
        await SendNotificationAsync(writer, "initialized", new { }, cancellationToken);

        await SendRequestAsync(writer, 2, "account/rateLimits/read", null, cancellationToken);
        using var rateLimitResponse = await ReadResponseAsync(reader, process, stderrTask, 2, cancellationToken);
        var rateLimits = ParseRateLimits(rateLimitResponse.RootElement);

        UsageSummary? usageSummary = null;
        try
        {
            await SendRequestAsync(writer, 3, "account/usage/read", null, cancellationToken);
            using var usageResponse = await ReadResponseAsync(reader, process, stderrTask, 3, cancellationToken);
            if (TryGetResult(usageResponse.RootElement, out var usageResult))
            {
                usageSummary = ParseUsage(usageResult);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Account usage is optional and older or transient app-server builds may not support it.
        }

        TryStop(process);
        _ = stderrTask;

        return rateLimits with
        {
            Usage = usageSummary
        };
    }

    private static Process StartAppServer()
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
            WorkingDirectory = GetStableWorkingDirectory()
        };

        AddShellPathHints(startInfo);

        if (TryUseInstalledCodexCli(startInfo))
        {
            startInfo.ArgumentList.Add("app-server");
        }
        else if (TryUseCachedCodexCli(startInfo))
        {
            startInfo.ArgumentList.Add("app-server");
        }
        else if (TryUseNodeNpx(startInfo))
        {
            AddCodexNpxArguments(startInfo);
        }
        else if (TryUseUnixNpx(startInfo))
        {
            AddCodexNpxArguments(startInfo);
        }
        else
        {
            startInfo.FileName = "npx";
            AddCodexNpxArguments(startInfo);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Codex app-server.");
    }

    private static string GetStableWorkingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            return home;
        }

        return Path.GetTempPath();
    }

    private static void AddShellPathHints(ProcessStartInfo startInfo)
    {
        var paths = new List<string>();
        var existingPath = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            paths.AddRange(existingPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        if (OperatingSystem.IsMacOS())
        {
            AddPathIfDirectoryExists(paths, "/opt/homebrew/bin");
            AddPathIfDirectoryExists(paths, "/opt/homebrew/sbin");
            AddPathIfDirectoryExists(paths, "/usr/local/bin");
            AddPathIfDirectoryExists(paths, "/usr/local/sbin");
            AddHomebrewNodePaths(paths, "/opt/homebrew/Cellar");
            AddHomebrewNodePaths(paths, "/usr/local/Cellar");
        }
        else if (!OperatingSystem.IsWindows())
        {
            AddPathIfDirectoryExists(paths, "/usr/local/bin");
            AddPathIfDirectoryExists(paths, "/usr/bin");
            AddPathIfDirectoryExists(paths, "/bin");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddPathIfDirectoryExists(paths, Path.Combine(home, ".npm-global", "bin"));
            AddPathIfDirectoryExists(paths, Path.Combine(home, ".local", "bin"));
            AddNvmNodePaths(paths, Path.Combine(home, ".nvm", "versions", "node"));
        }

        if (paths.Count > 0)
        {
            startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, paths.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    private static bool TryUseInstalledCodexCli(ProcessStartInfo startInfo)
    {
        var pathValue = startInfo.Environment.TryGetValue("PATH", out var path) ? path : null;
        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var codexPath = FindExecutableOnPath(executableName, pathValue);

        if (codexPath is null && OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                "/Applications/Codex.app/Contents/Resources/codex",
                "/Applications/ChatGPT.app/Contents/Resources/codex",
                Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex"),
                Path.Combine(home, "Applications", "ChatGPT.app", "Contents", "Resources", "codex")
            };
            codexPath = candidates.FirstOrDefault(File.Exists);
        }

        if (codexPath is null)
        {
            return false;
        }

        startInfo.FileName = codexPath;
        return true;
    }

    private static void AddPathIfDirectoryExists(List<string> paths, string path)
    {
        if (Directory.Exists(path) && !paths.Contains(path, StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }

    private static void AddNvmNodePaths(List<string> paths, string versionsPath)
    {
        if (!Directory.Exists(versionsPath))
        {
            return;
        }

        foreach (var nodeBinPath in Directory
            .EnumerateDirectories(versionsPath)
            .Select(path => Path.Combine(path, "bin"))
            .Where(Directory.Exists)
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)))
        {
            AddPathIfDirectoryExists(paths, nodeBinPath);
        }
    }

    private static void AddHomebrewNodePaths(List<string> paths, string cellarPath)
    {
        if (!Directory.Exists(cellarPath))
        {
            return;
        }

        foreach (var nodeFormulaPath in Directory
            .EnumerateDirectories(cellarPath, "node*")
            .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)))
        {
            foreach (var nodeBinPath in Directory
                .EnumerateDirectories(nodeFormulaPath)
                .Select(path => Path.Combine(path, "bin"))
                .Where(Directory.Exists)
                .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path)))
            {
                AddPathIfDirectoryExists(paths, nodeBinPath);
            }
        }
    }

    private static bool TryUseCachedCodexCli(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var nodePath = GetWindowsNodePath();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npxCachePath = Path.Combine(localAppData, "npm-cache", "_npx");

        if (nodePath is null || !Directory.Exists(npxCachePath))
        {
            return false;
        }

        var codexCliPath = Directory
            .EnumerateFiles(npxCachePath, "codex.js", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}@openai{Path.DirectorySeparatorChar}codex{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;

        if (codexCliPath is null)
        {
            return false;
        }

        startInfo.FileName = nodePath;
        startInfo.ArgumentList.Add(codexCliPath);
        return true;
    }

    private static bool TryUseNodeNpx(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var nodePath = GetWindowsNodePath();
        if (nodePath is null)
        {
            return false;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var npxCliPath = Path.Combine(programFiles, "nodejs", "node_modules", "npm", "bin", "npx-cli.js");

        if (!File.Exists(npxCliPath))
        {
            return false;
        }

        startInfo.FileName = nodePath;
        startInfo.ArgumentList.Add(npxCliPath);
        return true;
    }

    private static bool TryUseUnixNpx(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var npxPath = FindExecutableOnPath("npx", startInfo.Environment.TryGetValue("PATH", out var path) ? path : null);
        if (npxPath is not null)
        {
            startInfo.FileName = npxPath;
            return true;
        }

        var envPath = "/usr/bin/env";
        if (!File.Exists(envPath))
        {
            return false;
        }

        startInfo.FileName = envPath;
        startInfo.ArgumentList.Add("npx");
        return true;
    }

    private static string? FindExecutableOnPath(string executableName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? GetWindowsNodePath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var nodePath = Path.Combine(programFiles, "nodejs", "node.exe");
        return File.Exists(nodePath) ? nodePath : null;
    }

    private static void AddCodexNpxArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("@openai/codex@latest");
        startInfo.ArgumentList.Add("app-server");
    }

    private static async Task SendRequestAsync(
        StreamWriter writer,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var payload = parameters is null
            ? new Dictionary<string, object?> { ["id"] = id, ["method"] = method }
            : new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters };

        await writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task SendNotificationAsync(
        StreamWriter writer,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["method"] = method, ["params"] = parameters };
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        StreamReader reader,
        Process process,
        Task<string> stderrTask,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var readTask = reader.ReadLineAsync(cancellationToken).AsTask();
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(readTask, exitTask);

            if (completedTask == exitTask &&
                !readTask.IsCompletedSuccessfully)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stderr = await ReadAvailableStderrAsync(stderrTask);
                throw new EndOfStreamException(
                    $"Codex app-server exited before response {expectedId}. {stderr}");
            }

            var line = await readTask;
            if (line is null)
            {
                var stderr = await ReadAvailableStderrAsync(stderrTask);
                throw new EndOfStreamException(
                    $"Codex app-server closed stdout before response {expectedId}. {stderr}");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (document.RootElement.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.GetInt32() == expectedId)
            {
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "Unknown JSON-RPC error.";
                    throw new InvalidOperationException(message);
                }

                return document;
            }

            document.Dispose();
        }
    }

    private static async Task<string> ReadAvailableStderrAsync(Task<string> stderrTask)
    {
        if (!stderrTask.IsCompleted)
        {
            return string.Empty;
        }

        var stderr = await stderrTask;
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        const int maxLength = 1200;
        var trimmed = stderr.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[^maxLength..];
    }

    private static QuotaSnapshot ParseRateLimits(JsonElement response)
    {
        if (!TryGetResult(response, out var result))
        {
            throw new InvalidOperationException("Codex app-server did not return rate limit data.");
        }

        var limits = new List<QuotaLimit>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
            byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject())
            {
                limits.Add(ParseLimit(property.Value, property.Name));
            }
        }
        else if (result.TryGetProperty("rateLimits", out var singleLimit))
        {
            limits.Add(ParseLimit(singleLimit, "codex"));
        }

        limits = limits
            .OrderBy(limit => limit.LimitId == "codex" ? 0 : 1)
            .ThenBy(limit => limit.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new QuotaSnapshot(
            DateTimeOffset.Now,
            limits,
            ReadNullableInt(result, "rateLimitResetCredits", "availableCount"),
            null);
    }

    private static QuotaLimit ParseLimit(JsonElement element, string fallbackId)
    {
        var limitId = ReadString(element, "limitId") ?? fallbackId;
        var limitName = ReadString(element, "limitName");

        return new QuotaLimit(
            limitId,
            string.IsNullOrWhiteSpace(limitName) ? "Codex" : limitName,
            ReadString(element, "planType"),
            ReadWindow(element, "primary"),
            ReadWindow(element, "secondary"),
            ReadString(element, "rateLimitReachedType") is not null,
            ReadNullableBool(element, "credits", "hasCredits"),
            ReadNullableBool(element, "credits", "unlimited"));
    }

    private static QuotaWindow? ReadWindow(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var usedPercent = ReadInt(window, "usedPercent");
        return new QuotaWindow(
            usedPercent,
            Math.Clamp(100 - usedPercent, 0, 100),
            ReadInt(window, "windowDurationMins"),
            ReadUnixSeconds(window, "resetsAt"));
    }

    internal static UsageSummary ParseUsage(JsonElement element)
    {
        element.TryGetProperty("summary", out var summary);
        var dailyUsageBuckets = ReadDailyUsageBuckets(element);
        long? todayTokens = dailyUsageBuckets.Count == 0
            ? null
            : dailyUsageBuckets[^1].Tokens;

        return new UsageSummary(
            ReadNullableLong(summary, "lifetimeTokens"),
            ReadNullableLong(summary, "peakDailyTokens"),
            todayTokens,
            ReadNullableInt(summary, "currentStreakDays"),
            ReadNullableInt(summary, "longestStreakDays"),
            ReadNullableLong(summary, "longestRunningTurnSec"),
            dailyUsageBuckets);
    }

    private static IReadOnlyList<DailyUsageBucket> ReadDailyUsageBuckets(JsonElement element)
    {
        if (!element.TryGetProperty("dailyUsageBuckets", out var buckets) ||
            buckets.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DailyUsageBucket>();
        }

        var parsed = new List<DailyUsageBucket>();
        foreach (var bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var startDate = ReadString(bucket, "startDate");
            var tokens = ReadNullableLong(bucket, "tokens");
            if (string.IsNullOrWhiteSpace(startDate) || tokens is null)
            {
                continue;
            }

            parsed.Add(new DailyUsageBucket(startDate, Math.Max(0, tokens.Value)));
        }

        return parsed
            .OrderBy(bucket => bucket.StartDate, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryGetResult(JsonElement response, out JsonElement result)
    {
        return response.TryGetProperty("result", out result) &&
            result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : 0;
    }

    private static int? ReadNullableInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }

    private static int? ReadNullableInt(JsonElement element, string parent, string child)
    {
        return element.TryGetProperty(parent, out var parentElement)
            ? ReadNullableInt(parentElement, child)
            : null;
    }

    private static long? ReadNullableLong(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : null;
    }

    private static bool? ReadNullableBool(JsonElement element, string parent, string child)
    {
        if (!element.TryGetProperty(parent, out var parentElement) ||
            parentElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !parentElement.TryGetProperty(child, out var childElement) ||
            childElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return null;
        }

        return childElement.GetBoolean();
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(property.GetInt64())
            : null;
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Nothing useful to do if the short-lived probe already exited.
        }
    }

}
