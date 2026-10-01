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

public interface ICodexQuotaClient
{
    Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken);
}

public sealed class CodexQuotaClient : ICodexQuotaClient
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

        TryStop(process);
        _ = stderrTask;

        return rateLimits;
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

        if (!TryUseInstalledCodexCli(startInfo))
        {
            throw new InvalidOperationException(
                "Codex CLI was not found. Install or update ChatGPT/Codex, open it once, and refresh again.");
        }

        startInfo.ArgumentList.Add("app-server");

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
        var bundledCandidates = Array.Empty<string>();

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            bundledCandidates =
            [
                "/Applications/ChatGPT.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex",
                "/Applications/Codex.app/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex",
                Path.Combine(home, "Applications", "ChatGPT.app", "Contents", "Resources", "codex-cli", "CodexCLI.app", "Contents", "MacOS", "codex"),
                Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex-cli", "CodexCLI.app", "Contents", "MacOS", "codex"),
                "/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex",
                "/Applications/Codex.app/Contents/Resources/codex-cli/bin/codex",
                Path.Combine(home, "Applications", "ChatGPT.app", "Contents", "Resources", "codex-cli", "bin", "codex"),
                Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex-cli", "bin", "codex"),
                "/Applications/Codex.app/Contents/Resources/codex",
                "/Applications/ChatGPT.app/Contents/Resources/codex",
                Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex"),
                Path.Combine(home, "Applications", "ChatGPT.app", "Contents", "Resources", "codex")
            ];
        }

        var codexPath = SelectCodexExecutable(pathValue, bundledCandidates);

        if (codexPath is null)
        {
            return false;
        }

        startInfo.FileName = codexPath;
        return true;
    }

    internal static string? SelectCodexExecutable(
        string? pathValue,
        IEnumerable<string> bundledCandidates)
    {
        var bundledPath = bundledCandidates.FirstOrDefault(IsRunnableExecutable);
        if (bundledPath is not null)
        {
            return bundledPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var pathCandidate = FindExecutableOnPath(executableName, pathValue);
        return pathCandidate is not null &&
            IsRunnableExecutable(pathCandidate) &&
            !IsTransientNpxPath(pathCandidate)
            ? pathCandidate
            : null;
    }

    private static bool IsRunnableExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute;
            return (mode & executeBits) != 0;
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
    }

    private static bool IsTransientNpxPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.npm/_npx/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/npm-cache/_npx/", StringComparison.OrdinalIgnoreCase);
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
            ReadNullableInt(result, "rateLimitResetCredits", "availableCount"));
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
