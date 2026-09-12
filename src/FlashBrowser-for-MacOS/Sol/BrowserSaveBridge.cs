using System.Text.Json;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// P1.5 存档桥：把浏览器页面里 Ruffle 的存档（localStorage）接进查看器。
///
/// <para><b>存储事实（2026-09-12 从 Ruffle 0.6.0 bundle 反混淆 + 上游源码确证）：</b>
/// 值 = <c>base64(标准 .sol 文件字节)</c>（<c>web/src/storage.rs</c> 的
/// <c>LocalStorageBackend</c>，bundle 内 <c>zn</c>/<c>xn</c> 函数同证）；键 =
/// <c>{movie_host}/{movie_path}/{存档名}</c>（<c>avm1/globals/shared_object.rs</c>，
/// 存档名含 <c>/</c> 时加 <c>#</c> 前缀）。因此导出 = 取 base64 → 直接喂
/// <see cref="SolFile.Read"/>；导入 = 构造同形键 → 写回 base64。</para>
///
/// <para>委托由 <see cref="MainWindow"/> 用 CefGlue 的
/// <c>EvaluateJavaScript</c>（带返回值）/<c>ExecuteScript</c> 实现，
/// 查看器不感知浏览器实现。</para>
/// </summary>
public sealed class BrowserSaveBridge
{
    /// <summary>列出当前页面 localStorage 里的 Ruffle 存档，JSON 数组：[{"key","name","size"}]。</summary>
    public required Func<Task<string?>> ListSavesAsync { get; init; }

    /// <summary>按完整键取一个存档的 base64 内容；不存在或页面异常时返回 null/空。</summary>
    public required Func<string, Task<string?>> FetchSaveAsync { get; init; }

    /// <summary>把 base64 内容写到指定键（写完后需刷新页面才被游戏读到）。</summary>
    public required Func<string, string, Task> PutSaveAsync { get; init; }

    /// <summary>按页面里 Ruffle 正在播放的 movie URL 派生新存档键（与 Rust 的键构造一致）。</summary>
    public required Func<string, Task<string?>> DeriveKeyAsync { get; init; }

    private static string JsString(string value) => JsonSerializer.Serialize(value);

    /// <summary>列出页面存档的脚本。与 Ruffle <c>xn()</c> 相同的校验：base64 解码后
    /// <c>00 BF</c> + <c>"TCSO" 00 04 00 00 00 00</c>。</summary>
    public const string ListSavesScript = """
        return (function(){
            var out = [];
            try {
                for (var i = 0; i < localStorage.length; i++) {
                    var k = localStorage.key(i);
                    var v = localStorage.getItem(k);
                    if (typeof v !== 'string' || v.length < 16) continue;
                    try {
                        var bin = atob(v);
                        if (bin.charCodeAt(0) === 0 && bin.charCodeAt(1) === 0xBF
                            && bin.slice(6, 10) === 'TCSO') {
                            out.push({ key: k, name: k.split('/').pop(), size: bin.length });
                        }
                    } catch (e) {}
                }
            } catch (e) {}
            return JSON.stringify(out);
        })()
        """;

    /// <summary>取单个存档 base64 的脚本。</summary>
    public static string FetchSaveScript(string key) =>
        $"return (function(){{ var v = localStorage.getItem({JsString(key)}); return v === null ? '' : v; }})()";

    /// <summary>写入存档的语句（供 <c>ExecuteScript</c>，无返回值）。</summary>
    public static string PutSaveScript(string key, string base64Data) =>
        $"localStorage.setItem({JsString(key)}, {JsString(base64Data)})";

    /// <summary>按 movie URL 派生键的脚本。镜像上游
    /// <c>shared_object.rs</c>：<c>{movie_host}/{movie_path}/{prefix}{name}</c>。
    /// movie URL 依次尝试 <c>ruffle-player.swfUrl</c>、注入器暂存的
    /// <c>window.__fbSwfProxyUrl</c>（见 RuffleInjector.loadMainGame）、页面 URL。</summary>
    public static string DeriveKeyScript(string solName) => $$"""
        return (function(){
            var name = {{JsString(solName)}};
            try {
                var raw = '';
                try {
                    var p = document.querySelector('ruffle-player');
                    if (p && p.swfUrl) raw = String(p.swfUrl);
                } catch (e) {}
                if (!raw && window.__fbSwfProxyUrl) raw = String(window.__fbSwfProxyUrl);
                if (!raw) raw = document.location.href;
                var u = new URL(raw);
                var path = u.pathname.replace(/^\//, '');
                var prefix = name.indexOf('/') >= 0 ? '#' : '';
                return u.host + '/' + path + '/' + prefix + name;
            } catch (e) { return ''; }
        })()
        """;
}
