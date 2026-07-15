namespace UsageMonitor.Models;

public static class QuotaWindowLabels
{
    public static string FormatDuration(int minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
        {
            return $"{minutes / 1440}d";
        }

        if (minutes >= 60 && minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }
}
