using UsageMonitor.Services;
using Xunit;

namespace UsageMonitor.Tests;

public sealed class CodexQuotaClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"UsageMonitor.CodexQuotaClientTests.{Guid.NewGuid():N}");

    [Fact]
    public void SelectCodexExecutable_PrefersBundledCliOverPath()
    {
        var bundledPath = CreateExecutable("bundle", ExecutableName);
        var pathDirectory = Path.Combine(_directory, "path");
        CreateExecutable("path", ExecutableName);

        var selected = CodexQuotaClient.SelectCodexExecutable(
            pathDirectory,
            [bundledPath]);

        Assert.Equal(bundledPath, selected);
    }

    [Fact]
    public void SelectCodexExecutable_RejectsTransientNpxCache()
    {
        var npxBin = Path.Combine(_directory, ".npm", "_npx", "cache-key", "node_modules", ".bin");
        CreateExecutable(npxBin, ExecutableName, isAbsoluteDirectory: true);

        var selected = CodexQuotaClient.SelectCodexExecutable(
            npxBin,
            []);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectCodexExecutable_AcceptsStablePathInstall()
    {
        var pathDirectory = Path.Combine(_directory, "stable", "bin");
        var executablePath = CreateExecutable(pathDirectory, ExecutableName, isAbsoluteDirectory: true);

        var selected = CodexQuotaClient.SelectCodexExecutable(
            pathDirectory,
            []);

        Assert.Equal(executablePath, selected);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "codex.exe" : "codex";

    private string CreateExecutable(string directory, string fileName, bool isAbsoluteDirectory = false)
    {
        var resolvedDirectory = isAbsoluteDirectory
            ? directory
            : Path.Combine(_directory, directory);
        Directory.CreateDirectory(resolvedDirectory);
        var path = Path.Combine(resolvedDirectory, fileName);
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
