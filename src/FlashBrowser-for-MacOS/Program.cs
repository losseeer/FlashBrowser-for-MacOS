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
    /// CEF on macOS does not allow multiple processes to share a cache directory;
    /// using a per-launch unique path avoids "files in use" conflicts when running
    /// the app multiple times in dev.
    /// </summary>
    public static int Main(string[] args)
    {
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            "FlashBrowserForMacOS_" + Guid.NewGuid().ToString("N"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(cachePath);

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .AfterSetup(_ => CefRuntimeLoader.Initialize(
                new CefSettings
                {
                    RootCachePath = cachePath,
                    // WindowlessRenderingEnabled = false → use the OS-native window
                    // (on macOS this gives us AppKit-backed surfaces with proper HW accel).
                    WindowlessRenderingEnabled = false
                },
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
