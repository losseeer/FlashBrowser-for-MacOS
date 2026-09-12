using Avalonia;
using FlashBrowserForMacOS.Ruffle;
using System;
using System.IO;
using Xilium.CefGlue;                 // CefSettings, CefRuntime
using Xilium.CefGlue.Common;          // CefRuntimeLoader
using Xilium.CefGlue.Common.Shared;   // CustomScheme

namespace FlashBrowserForMacOS;

internal static class Program
{
    /// <summary>
    /// P1.4 dev convenience: <c>--sol=&lt;path&gt;</c> opens the .sol viewer on that file
    /// at startup. Exists so a GUI run can be made self-describing (the viewer logs what
    /// it loaded into FB_DIAG_LOG) without anyone clicking through the file picker.
    /// </summary>
    public static string? LaunchSolPath { get; private set; }

    /// <summary>
    /// CEF storage root.
    ///
    /// <para><b>Persistent (default)</b>: <c>~/Library/Application Support/FlashBrowserForMacOS/CEF</c>.
    /// Required by P1.5 — the embedded Ruffle writes game saves into localStorage, which
    /// lives inside this profile; a per-launch temp directory meant every save was wiped
    /// on exit. Cookies/sessions also survive, as a normal browser would.</para>
    ///
    /// <para><b>Temp (<c>--temp-profile</c>)</b>: the old behaviour — a unique directory per
    /// launch, deleted on exit. Kept as a dev escape hatch because CEF on macOS does not
    /// allow multiple processes to share a cache directory ("files in use" conflicts), so
    /// a second app instance must be launched with this flag.</para>
    /// </summary>
    private static string BuildCachePath(string[] args)
    {
        if (GetOption(args, "--temp-profile") is not null)
        {
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                "FlashBrowserForMacOS_" + Guid.NewGuid().ToString("N"));
            Diagnostics.Log($"cef profile: TEMP {tempPath}");
            return tempPath;
        }

        var persistentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlashBrowserForMacOS",
            "CEF");
        Directory.CreateDirectory(persistentPath);
        Diagnostics.Log($"cef profile: persistent {persistentPath}");
        return persistentPath;
    }

    public static int Main(string[] args)
    {
        Diagnostics.Initialize(GetOption(args, "--diag-log"));
        LaunchSolPath = GetOption(args, "--sol");

        var cachePath = BuildCachePath(args);

        // Only the temp profile is disposable; deleting the persistent one would
        // throw away game saves — the exact problem P1.5 exists to fix.
        if (GetOption(args, "--temp-profile") is not null)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(cachePath);
        }

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterSetup(_ => CefRuntimeLoader.Initialize(
                BuildCefSettings(cachePath, GetOption(args, "--debug-port")),
                customSchemes: new[]
                {
                    // Serves Ruffle (Flash emulator) assets: ruffle.js + core.*.js + *.wasm.
                    // IsCorsEnabled + IsFetchEnabled are required so the page's JS can
                    // fetch the wasm from the custom scheme.
                    new CustomScheme
                    {
                        SchemeName = RuffleSchemeHandlerFactory.SchemeName,
                        DomainName = RuffleSchemeHandlerFactory.DomainName,
                        IsStandard = true,
                        IsSecure = true,
                        IsCorsEnabled = true,
                        IsCSPBypassing = false,
                        IsFetchEnabled = true,
                        SchemeHandlerFactory = new RuffleSchemeHandlerFactory()
                    },
                    // Proxies cross-origin SWF downloads so Ruffle can fetch 4399's
                    // SWF (which lacks CORS) through a CORS-enabled scheme.
                    new CustomScheme
                    {
                        SchemeName = SwfProxySchemeHandlerFactory.SchemeName,
                        DomainName = SwfProxySchemeHandlerFactory.DomainName,
                        IsStandard = true,
                        IsSecure = true,
                        IsCorsEnabled = true,
                        IsCSPBypassing = false,
                        IsFetchEnabled = true,
                        SchemeHandlerFactory = new SwfProxySchemeHandlerFactory()
                    }
                }))
            .StartWithClassicDesktopLifetime(args);

        return 0;
    }

    /// <summary>
    /// Reads <c>--name=value</c> from the process arguments, or null when absent.
    ///
    /// <para>Needed because these two dev-only options have to survive an
    /// <c>open --args</c> launch, where the environment cannot be injected (<c>launchctl
    /// setenv</c> is refused without privileges and <c>open --env</c> is not honoured).</para>
    /// </summary>
    private static string? GetOption(string[] args, string name)
    {
        var prefix = name + "=";

        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the CEF settings for this launch.
    /// </summary>
    private static CefSettings BuildCefSettings(string cachePath, string? debugPortText)
    {
        var settings = new CefSettings
        {
            RootCachePath = cachePath,
            // WindowlessRenderingEnabled = false → use the OS-native window
            // (on macOS this gives us AppKit-backed surfaces with proper HW accel).
            WindowlessRenderingEnabled = false
        };

        // [DEV ONLY] --debug-port=<port> (or FB_DEBUG_PORT) opens CEF's DevTools
        // remote-debugging endpoint on 127.0.0.1:<port>, which is what turns a GUI run into
        // something a script can inspect. It has to go through this settings field: CefGlue
        // builds CefMainArgs from the executable name alone (CefRuntimeLoader.cs:92), so a
        // `--remote-debugging-port=` switch never reaches CEF.
        if (int.TryParse(debugPortText ?? Environment.GetEnvironmentVariable("FB_DEBUG_PORT"), out var debugPort))
        {
            settings.RemoteDebuggingPort = debugPort;
        }

        return settings;
    }

    private static void Shutdown(string cachePath)
    {
        try
        {
            // must shutdown CEF before deleting the cache, otherwise file locks
            // prevent the cleanup and Temp fills up.
            CefRuntime.Shutdown();
        }
        catch { /* already shut down */ }

        try
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
        catch { /* best effort cleanup */ }
    }
}
