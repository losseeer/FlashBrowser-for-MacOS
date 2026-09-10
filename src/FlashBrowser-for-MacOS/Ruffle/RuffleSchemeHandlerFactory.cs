using System.IO;
using System.Text;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace FlashBrowserForMacOS.Ruffle;

/// <summary>
/// Serves the Ruffle (Flash emulator) self-hosted assets over a custom
/// <c>ruffle://app/...</c> scheme.
///
/// Ruffle's ruffle.js resolves its core chunk + wasm files relative to its
/// own URL (webpack publicPath), so serving all files from a single scheme
/// directory is enough for the loader to find them.
/// </summary>
public sealed class RuffleSchemeHandlerFactory : CefSchemeHandlerFactory
{
    public const string SchemeName = "ruffle";
    public const string DomainName = "app";

    private static readonly string AssetRoot =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Ruffle");

    protected override CefResourceHandler Create(
        CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
    {
        // request.Url looks like: ruffle://app/ruffle.js
        // Strip "ruffle://app/" to get the bare file name.
        var url = request.Url;
        var fileName = ExtractFileName(url);

        var filePath = Path.Combine(AssetRoot, fileName);

        if (!File.Exists(filePath))
        {
            return NotFound();
        }

        // Static assets, loaded once per session. Reading into memory avoids
        // managing FileStream lifetime inside CEF's IO-thread handler.
        var bytes = File.ReadAllBytes(filePath);

        return new DefaultResourceHandler
        {
            Status = 200,
            StatusText = "OK",
            MimeType = GetMimeType(fileName),
            Response = new MemoryStream(bytes)
        };
    }

    private static string ExtractFileName(string url)
    {
        // "ruffle://app/ruffle.js" -> "ruffle.js"
        var path = url;
        var schemeMarker = url.IndexOf("//", StringComparison.Ordinal);
        if (schemeMarker >= 0)
        {
            path = url.Substring(schemeMarker + 2); // "app/ruffle.js"
        }

        // Path.GetFileName is safe against "../" traversal (returns last segment).
        return Path.GetFileName(path);
    }

    private static CefResourceHandler NotFound()
    {
        var body = Encoding.UTF8.GetBytes("404 Not Found");
        return new DefaultResourceHandler
        {
            Status = 404,
            StatusText = "Not Found",
            MimeType = "text/plain",
            Response = new MemoryStream(body)
        };
    }

    private static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            // CRITICAL: wasm must be served as application/wasm, otherwise
            // Chromium rejects it with "Incorrect response MIME type".
            ".wasm" => "application/wasm",
            ".js" => "text/javascript",
            ".map" => "application/json",
            _ => "application/octet-stream"
        };
    }
}
