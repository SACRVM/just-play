using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using JustPlay.App.Views;
using JustPlay.Core.Storage;

namespace JustPlay.App;

/// <summary>
/// App-wide crash safety net. Anything that escapes a local try/catch - a UI-thread exception, a
/// background-task fault, or an AppDomain-fatal throw - is funnelled here: written to a crash log AND
/// shown in a copyable "Oops" dialog, instead of the app dying silently. We stand by our bugs: the
/// dialog hands the user the full report with a Copy button so they can send it to us.
/// </summary>
public static class ErrorReporter
{
    // 0/1 guard so a burst of errors shows ONE dialog at a time, not a storm of windows.
    private static int _showing;

    /// <summary>Full human-readable report: app version, time, OS/.NET, context, and the exception
    /// (type + message + stack + inner exceptions via <see cref="Exception.ToString"/>).</summary>
    public static string BuildReport(Exception? ex, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"JustPlay {AppInfo.DisplayVersion} - error report");
        sb.AppendLine($"When:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS:    {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET:  {RuntimeInformation.FrameworkDescription}");
        if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine($"Where: {context}");
        sb.AppendLine();
        sb.AppendLine(ex?.ToString() ?? "(no exception object)");
        return sb.ToString();
    }

    /// <summary>Append the report to a per-day log under %LOCALAPPDATA%/JustPlay/logs. Never throws.</summary>
    public static void LogToFile(string report)
    {
        try
        {
            var dir = JustDataPaths.Combine("JustPlay", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, report + Environment.NewLine + new string('-', 72) + Environment.NewLine);
        }
        catch { /* logging itself must never throw */ }
    }

    /// <summary>Log the error and show the copyable Oops dialog (marshalled to the UI thread). Safe to
    /// call from ANY thread, and safe before the UI is up (then it just logs).</summary>
    public static void Report(Exception? ex, string? context = null)
    {
        var report = BuildReport(ex, context);
        LogToFile(report);

        if (Application.Current is null) return;             // pre-UI: logged, no dialog possible yet
        if (Dispatcher.UIThread.CheckAccess()) ShowDialog(report);
        else Dispatcher.UIThread.Post(() => ShowDialog(report));
    }

    private static void ShowDialog(string report)
    {
        if (Interlocked.Exchange(ref _showing, 1) == 1) return;   // already showing one
        try
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var dlg = new OopsDialog(report);
            dlg.Closed += (_, _) => Interlocked.Exchange(ref _showing, 0);
            if (owner is { IsVisible: true }) _ = dlg.ShowDialog(owner);
            else dlg.Show();
        }
        catch
        {
            Interlocked.Exchange(ref _showing, 0);   // never let the reporter itself crash the app
        }
    }
}
