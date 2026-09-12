using System;
using System.IO;

namespace FlashBrowserForMacOS;

/// <summary>
/// Opt-in diagnostics for GUI runs.
///
/// <para><b>Why this exists.</b> A GUI run of this app is otherwise unobservable from a
/// terminal. CEF's own stderr logging does not carry the page's console output in this
/// setup, and Chromium command-line switches never reach CEF at all: CefGlue builds
/// <c>CefMainArgs</c> from the executable name only (CefRuntimeLoader.cs:92), so
/// <c>--enable-logging</c> / <c>--log-net-log</c> / <c>--remote-debugging-port</c> are
/// silently discarded. On a machine where screen capture is denied (no macOS Screen
/// Recording permission) that leaves nothing at all to inspect.</para>
///
/// <para>Set <c>FB_DIAG_LOG</c> to a file path to mirror browser lifecycle events and page
/// console messages into that file. With the variable unset every call is a no-op, so
/// normal runs neither allocate nor touch the disk.</para>
/// </summary>
internal static class Diagnostics
{
    private static readonly object Gate = new();

    private static string? _logPath;

    /// <summary>
    /// Selects the destination for diagnostic output. Prefers <paramref name="explicitPath"/>
    /// (from <c>--diag-log=</c> on the command line) and falls back to the
    /// <c>FB_DIAG_LOG</c> environment variable. Called once from <c>Main</c>; with neither
    /// source present the log stays off.
    ///
    /// <para>The command-line form exists because the environment cannot be injected into an
    /// <c>open</c>-launched instance here (<c>launchctl setenv</c> is refused without
    /// privileges, and <c>open --env</c> is not honoured), while <c>open --args</c> does
    /// reach the process.</para>
    /// </summary>
    public static void Initialize(string? explicitPath = null)
    {
        _logPath = string.IsNullOrWhiteSpace(explicitPath)
            ? Environment.GetEnvironmentVariable("FB_DIAG_LOG")
            : explicitPath;
    }

    /// <summary>True when a destination is configured.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(_logPath);

    /// <summary>
    /// [DEV ONLY] Opt-in for the P0-0 pump probe: drive <c>CefRuntime.DoMessageLoopWork()</c>
    /// from the Avalonia dispatcher by hand after a delay, so a single run shows the browser
    /// failing to initialise before the pump is started and (hopefully) initialising after.
    ///
    /// <para>Deliberately separate from <see cref="Enabled"/>: the default diagnostic run must
    /// still reproduce the unmodified behaviour, otherwise the baseline disappears. Set
    /// <c>FB_DIAG_PUMP</c> to run it.</para>
    /// </summary>
    public static bool PumpProbe => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("FB_DIAG_PUMP"));

    /// <summary>Appends one timestamped line. Never throws: diagnostics must not be able to
    /// break the app it is measuring.</summary>
    public static void Log(string message)
    {
        var path = _logPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, line);
            }
            catch
            {
                // Intentionally swallowed — see the summary.
            }
        }
    }
}
