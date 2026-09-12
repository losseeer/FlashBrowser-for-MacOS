using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace FlashBrowserForMacOS.Ruffle;

/// <summary>
/// Serves cross-origin resources (SWFs and the XML/GIF/SWF control files that
/// Ruffle's internal URLLoader pulls at runtime) through a CORS-enabled proxy
/// scheme, because 4399's resource hosts lack <c>Access-Control-Allow-Origin</c>.
///
/// URL shape: <c>swfproxy://app/load?u=&lt;urlencoded absolute resource URL&gt;</c>
///
/// The handler downloads the target server-side (with a 4399 <c>Referer</c> +
/// browser User-Agent to defeat anti-hotlink) and returns the bytes with CORS
/// headers, sidestepping the browser's same-origin fetch restriction.
/// </summary>
public sealed class SwfProxySchemeHandlerFactory : CefSchemeHandlerFactory
{
    public const string SchemeName = "swfproxy";
    public const string DomainName = "app";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    protected override CefResourceHandler Create(
        CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
    {
        var target = ExtractTargetUrl(request.Url);
        if (target is null || !IsAllowed(target))
        {
            return Error(400, "Bad Request");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            // Some 4399 SWF CDN paths reject requests without a 4399 Referer.
            req.Headers.Referrer = new Uri("https://www.4399.com/");

            // Blocking on CEF's IO thread is acceptable for a single-page browser.
            // TODO: swap for an async CefResourceHandler if concurrent loads matter.
            using var resp = Client.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode)
            {
                return Error((int)resp.StatusCode, "Upstream error");
            }

            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return Resource(bytes, resp.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception ex)
        {
            return Error(502, "Proxy failed: " + ex.Message);
        }
    }

    private static string? ExtractTargetUrl(string url)
    {
        // "swfproxy://app/load?u=<urlencoded target>"
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return null;
        }

        var query = url.Substring(q + 1);
        if (!query.StartsWith("u=", StringComparison.Ordinal))
        {
            return null;
        }

        return Uri.UnescapeDataString(query.Substring(2));
    }

    private static bool IsAllowed(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serves a proxied resource with the upstream media type. Ruffle's internal
    /// URLLoader pulls more than SWFs (control XML, tracking GIFs, ad SWFs), so the
    /// proxy must not hard-code the Flash MIME type; when upstream omits it, fall
    /// back to the SWF type that the scheme originally served.
    /// </summary>
    private static CefResourceHandler Resource(byte[] bytes, string? mediaType)
    {
        var headers = new NameValueCollection
        {
            ["Access-Control-Allow-Origin"] = "*"
        };

        return new DefaultResourceHandler
        {
            Status = 200,
            StatusText = "OK",
            MimeType = string.IsNullOrWhiteSpace(mediaType)
                ? "application/x-shockwave-flash"
                : mediaType,
            Headers = headers,
            Response = new MemoryStream(bytes)
        };
    }

    private static CefResourceHandler Error(int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        return new DefaultResourceHandler
        {
            Status = status,
            StatusText = "Error",
            MimeType = "text/plain",
            Response = new MemoryStream(body)
        };
    }
}
