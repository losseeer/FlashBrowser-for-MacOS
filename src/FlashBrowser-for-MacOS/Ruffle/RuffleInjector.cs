namespace FlashBrowserForMacOS.Ruffle;

/// <summary>
/// Injects Ruffle (Flash emulator) into target pages.
///
/// Strategy (honed against 4399's real page structure):
/// <list type="bullet">
/// <item>
/// We do NOT call <c>ruffle.polyfill()</c>. It recursively scans every iframe and
/// trips over the cross-origin <c>ptlogin.4399.com</c> login iframe (which embeds a
/// Flash login control). The failed polyfill destroys the Ruffle instance, so the
/// main SWF then loads against a null instance (<c>stream_from</c> error).
/// </item>
/// <item>
/// Instead we manually create a single Ruffle player and load only the main game SWF,
/// so Ruffle never touches iframes and the instance survives.
/// </item>
/// <item>
/// We still call <c>ruffle.pluginPolyfill()</c> to fake <c>navigator.plugins</c>, which
/// keeps 4399's own Flash-detection script (<c>flashopen1.js</c>) from showing its
/// "install Flash" overlay.
/// </item>
/// </list>
/// </summary>
public static class RuffleInjector
{
    private const string RuffleScriptUrl = "ruffle://app/ruffle.js";

    // The bootstrap JS contains lots of { } braces, so we use a plain
    // (non-interpolated) raw string and swap a token for the script URL.
    private const string UrlToken = "__RUFFLE_SCRIPT_URL__";

    /// <summary>
    /// True if Ruffle should be injected into the given URL.
    /// Only 4399 Flash game pages are targeted for now.
    /// </summary>
    public static bool ShouldInject(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return url.Contains("4399.com/flash/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the early-injection JavaScript, executed at V8 context creation time
    /// (via <c>JavascriptContextCreated</c>) — i.e. BEFORE the page's own scripts
    /// (including 4399's <c>flashopen1.js</c> Flash-detection) run.
    ///
    /// It fakes <c>navigator.plugins</c> / <c>navigator.mimeTypes</c> so 4399's
    /// <c>hasUsableFlash()</c> passes, and installs a MutationObserver that only
    /// logs (does not mutate) when a <c>ruffle-player</c> is torn out of the DOM —
    /// this gives us ground truth about which script removes the player.
    /// </summary>
    public static string BuildEarlyInjectionScript()
    {
        // Plain raw string literal: every { and } is literal, no escaping needed.
        const string script = """
            (function() {
                if (window.__ruffleEarlyInjected) return;
                window.__ruffleEarlyInjected = true;

                function makeFlashPlugin() {
                    return {
                        "name": "Shockwave Flash",
                        "description": "Shockwave Flash 32.0 r0",
                        "filename": "ruffle.js"
                    };
                }

                // Chrome reports navigator.plugins as a read-only frozen empty
                // PluginArray; defineProperty overrides it when configurable.
                function spoofPlugins() {
                    var flash = makeFlashPlugin();
                    var plugins = [flash];
                    plugins.namedItem = function(n) { return n === "Shockwave Flash" ? flash : null; };
                    plugins.item = function(i) { return i === 0 ? flash : null; };
                    plugins.refresh = function() {};
                    plugins["Shockwave Flash"] = flash;   // array-style lookup used by 4399
                    plugins.length = 1;
                    Object.defineProperty(navigator, "plugins", { value: plugins, configurable: true });

                    var mime = { "type": "application/x-shockwave-flash", "suffixes": "swf", "description": "Shockwave Flash", "enabledPlugin": flash };
                    var mimes = [mime];
                    mimes.namedItem = function(n) { return n === "application/x-shockwave-flash" ? mime : null; };
                    mimes.item = function(i) { return i === 0 ? mime : null; };
                    mimes["application/x-shockwave-flash"] = mime;
                    mimes.length = 1;
                    Object.defineProperty(navigator, "mimeTypes", { value: mimes, configurable: true });
                }

                try {
                    spoofPlugins();
                    console.log("[Ruffle-early] navigator.plugins spoofed");
                } catch (e) {
                    console.error("[Ruffle-early] spoof failed: " + e);
                }

                // Diagnostic only: log when a ruffle-player is removed from the DOM
                // and from which container, so we can identify the culprit script.
                try {
                    var mo = new MutationObserver(function(mutations) {
                        mutations.forEach(function(m) {
                            if (!m.removedNodes) return;
                            m.removedNodes.forEach(function(n) {
                                if (n && n.nodeType === 1) {
                                    var isPlayer = n.tagName === "RUFFLE-PLAYER";
                                    var hasPlayer = !!n.querySelector && n.querySelector("ruffle-player");
                                    if (isPlayer || hasPlayer) {
                                        var t = m.target;
                                        var where = (t && (t.id || t.tagName)) ? (t.id || t.tagName) : "unknown";
                                        console.log("[Ruffle-early] ruffle-player REMOVED from DOM under <" + where + "> at " + Date.now());
                                    }
                                }
                            });
                        });
                    });
                    mo.observe(document.documentElement, { childList: true, subtree: true });
                } catch (e) {
                    console.error("[Ruffle-early] observer failed: " + e);
                }
            })();
            """;

        return script;
    }

    /// <summary>
    /// Builds the bootstrap JavaScript injected into the page.
    /// </summary>
    public static string BuildInjectionScript()
    {
        // Plain raw string literal: every { and } is literal, no escaping needed.
        const string script = """
            (function() {
                if (window.__ruffleInjected) return;
                window.__ruffleInjected = true;

                window.RufflePlayer = window.RufflePlayer || {};
                window.RufflePlayer.config = Object.assign({}, window.RufflePlayer.config, {
                    "autoplay": "on",
                    "unmuteOverlay": "hidden",
                    "polyfills": false,
                    "letterbox": "on"
                });

                // Route a .swf URL through the CORS-enabled proxy scheme so the
                // cross-origin fetch succeeds (sda.4399.com has no ACAO header).
                function toProxy(raw) {
                    if (!raw) return raw;
                    if (/^swfproxy:\/\//i.test(raw)) return raw;
                    try {
                        var abs = new URL(raw, window.location.href).href;
                        return 'swfproxy://app/load?u=' + encodeURIComponent(abs);
                    } catch (e) {
                        return raw;
                    }
                }

                // Extract flashvars (session params some games need) from the embed/object.
                function extractFlashvars(el) {
                    var fv = {};
                    var raw = el.getAttribute('flashvars');
                    if (!raw) {
                        var p = el.querySelector('param[name="flashvars"]');
                        if (p) raw = p.getAttribute('value');
                    }
                    if (raw) {
                        raw.split('&').forEach(function(kv) {
                            var i = kv.indexOf('=');
                            if (i > 0) fv[decodeURIComponent(kv.slice(0, i))] = decodeURIComponent(kv.slice(i + 1));
                        });
                    }
                    return fv;
                }

                // 4399 injects a 10x10 Flash-detection SWF (objtest.swf, from flashopen1.js)
                // hidden in #testflashplayer. Remove it so Ruffle only sees the game.
                function removeTestFlashPlayers() {
                    var tp = document.getElementById('testflashplayer');
                    if (tp && tp.parentNode) tp.parentNode.removeChild(tp);
                    document.querySelectorAll('embed, object').forEach(function(el) {
                        var src = el.getAttribute('src') || el.getAttribute('data') || '';
                        var p = el.querySelector('param[name="movie"], param[name="src"]');
                        if (p) src = src || (p.getAttribute('value') || '');
                        if (/objtest\.swf|cell\.swf|test\.swf/i.test(src)) {
                            if (el.parentNode) el.parentNode.removeChild(el);
                        }
                    });
                }

                // Create ONE Ruffle player for the main game SWF and load it directly,
                // bypassing polyfill's iframe recursion (which destroys the instance).
                function loadMainGame(ruffle) {
                    var el = document.getElementById('flashgame1');
                    if (!el) {
                        var all = document.querySelectorAll('embed, object');
                        for (var i = 0; i < all.length; i++) {
                            var s = all[i].getAttribute('src') || all[i].getAttribute('data') || '';
                            if (/upload_swf|main\.swf/i.test(s)) { el = all[i]; break; }
                        }
                    }
                    if (!el) return false;

                    var swfUrl = el.getAttribute('src') || el.getAttribute('data') || '';
                    var proxyUrl = toProxy(swfUrl);
                    var fv = extractFlashvars(el);

                    var player = ruffle.createPlayer();
                    var w = el.getAttribute('width'), h = el.getAttribute('height');
                    if (w) player.style.width = /^\d+$/.test(w) ? w + 'px' : w;
                    if (h) player.style.height = /^\d+$/.test(h) ? h + 'px' : h;

                    // Replace the whole <object> host (not just the inner <embed>) so the
                    // ruffle-player is a direct child of the game container, not a fallback
                    // child of a Flash <object> that may get clobbered by the page.
                    var host = el.parentNode;
                    if (host && host.nodeName === 'OBJECT') {
                        host.parentNode.replaceChild(player, host);
                    } else {
                        el.parentNode.replaceChild(player, el);
                    }

                    var opts = { url: proxyUrl };
                    if (Object.keys(fv).length) opts.parameters = fv;
                    player.load(opts);
                    return true;
                }

                var s = document.createElement('script');
                s.src = '__RUFFLE_SCRIPT_URL__';
                s.onload = function() {
                    try {
                        removeTestFlashPlayers();
                        var ruffle = window.RufflePlayer && window.RufflePlayer.newest();
                        if (ruffle) {
                            // Fake navigator.plugins so 4399's flashopen1.js detection passes.
                            if (typeof ruffle.pluginPolyfill === 'function') ruffle.pluginPolyfill();
                            loadMainGame(ruffle);
                        }
                    } catch (e) {
                        console.error('[Ruffle] injection error:', e);
                    }
                };
                s.onerror = function() {
                    console.error('[Ruffle] failed to load ' + s.src);
                };
                (document.head || document.documentElement).appendChild(s);
            })();
            """;

        return script.Replace(UrlToken, RuffleScriptUrl);
    }
}
