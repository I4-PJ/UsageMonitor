using Avalonia;
using Avalonia.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UsageMonitor.Services;

namespace UsageMonitor
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
            {
                var lines = new List<string>();
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var snapshot = new CodexQuotaClient().ReadQuotaAsync(timeout.Token).GetAwaiter().GetResult();

                    foreach (var limit in snapshot.Limits)
                    {
                        lines.Add($"{limit.DisplayName} ({limit.LimitId})");
                        if (limit.Primary is not null)
                        {
                            lines.Add($"  5h: {limit.Primary.LeftPercent}% left, resets {limit.Primary.ResetsAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                        }

                        if (limit.Secondary is not null)
                        {
                            lines.Add($"  7d: {limit.Secondary.LeftPercent}% left, resets {limit.Secondary.ResetsAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                        }
                    }

                    lines.Add(snapshot.AvailableResetCredits is null
                        ? "Reset credits: unknown"
                        : $"Reset credits: {snapshot.AvailableResetCredits}");
                }
                catch (Exception ex)
                {
                    lines.Add(ex.ToString());
                }

                var probePath = Path.Combine(Path.GetTempPath(), "UsageMonitor.probe.txt");
                File.WriteAllLines(probePath, lines);
                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }

                return;
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new Win32PlatformOptions
                {
                    CompositionMode =
                    [
                        Win32CompositionMode.RedirectionSurface
                    ]
                });
    }
}
