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

                // Mount the player on a stage of our own, appended straight to <html> and
                // therefore OUTSIDE <body>, instead of inside the page's game container.
                //
                // Why: Ruffle 0.6.0's load() requires the element to be connected
                // ("if (this.element.isConnected && ...)"), so we cannot load it while
                // detached. But the element also destroys itself in disconnectedCallback(),
                // and load() awaits ensureFreshInstance() BEFORE it ever reaches
                // stream_from()/load_data(). That await yields the event loop for at least
                // 200ms (a setTimeout inside ensureFreshInstance). If the host page removes
                // the player during that window, disconnectedCallback -> destroy() nulls
                // this.instance, and the load dies with "reading 'stream_from'" of null.
                //
                // Passing the SWF bytes as `data` does NOT help: the removal happens inside
                // ensureFreshInstance, i.e. before load_data() is called at all. The only
                // fix is to keep the player somewhere the page does not manage, so nothing
                // it does to its own game container (or to <body>) can detach us.
                var OWN_STAGE_ID = '__ruffle_stage';

                function createOwnStage(rect) {
                    var prev = document.getElementById(OWN_STAGE_ID);
                    if (prev && prev.parentNode) prev.parentNode.removeChild(prev);

                    var stage = document.createElement('div');
                    stage.id = OWN_STAGE_ID;
                    stage.style.position = 'absolute';
                    stage.style.zIndex = '2147483647';
                    stage.style.background = '#000';

                    // Cover the box the original <object>/<embed> occupied. Fall back to the
                    // viewport when the element reports no box (hidden / zero-sized), so the
                    // player is never parked off-screen at 0x0.
                    if (rect && rect.width > 0 && rect.height > 0) {
                        stage.style.left   = (rect.left + window.scrollX) + 'px';
                        stage.style.top    = (rect.top  + window.scrollY) + 'px';
                        stage.style.width  = rect.width  + 'px';
                        stage.style.height = rect.height + 'px';
                    } else {
                        stage.style.position = 'fixed';
                        stage.style.left = '0';
                        stage.style.top = '0';
                        stage.style.width = '100vw';
                        stage.style.height = '100vh';
                    }

                    document.documentElement.appendChild(stage);
                    return stage;
                }

                // The SWF address an element carries, including the <param name="movie">
                // form used by an <object>.
                function swfSourceOf(el) {
                    if (!el || !el.getAttribute) return '';
                    var src = el.getAttribute('src') || el.getAttribute('data') || '';
                    if (!src && el.querySelector) {
                        var p = el.querySelector('param[name="movie"], param[name="src"]');
                        if (p) src = p.getAttribute('value') || '';
                    }
                    return src;
                }

                // Locate the element that actually carries the SWF. #flashgame1 is sometimes
                // the <embed>/<object> itself and sometimes just a container wrapping them;
                // a container has no src/data of its own, so taking it at face value yields
                // an empty SWF URL and the load silently targets nothing.
                function findFlashHost() {
                    var byId = document.getElementById('flashgame1');
                    var found = document.querySelectorAll('embed, object');
                    var ordered = byId ? [byId].concat([].slice.call(found)) : [].slice.call(found);

                    var fallback = null;
                    for (var i = 0; i < ordered.length; i++) {
                        var src = swfSourceOf(ordered[i]);
                        if (!src) continue;
                        if (/upload_swf|main\.swf/i.test(src)) return ordered[i];
                        if (!fallback) fallback = ordered[i];
                    }
                    return fallback;
                }

                // Create ONE Ruffle player for the main game SWF and load it directly,
                // bypassing polyfill's iframe recursion (which destroys the instance).
                function loadMainGame(ruffle) {
                    var el = findFlashHost();
                    if (!el) return false;

                    var swfUrl = swfSourceOf(el);
                    var proxyUrl = toProxy(swfUrl);
                    var fv = extractFlashvars(el);

                    var player = ruffle.createPlayer();
                    // Measure the original host BEFORE detaching it.
                    var rect = el.getBoundingClientRect();
                    var w = el.getAttribute('width'), h = el.getAttribute('height');
                    if (w) player.style.width = /^\d+$/.test(w) ? w + 'px' : w;
                    if (h) player.style.height = /^\d+$/.test(h) ? h + 'px' : h;
                    if (!w && rect.width) player.style.width = rect.width + 'px';
                    if (!h && rect.height) player.style.height = rect.height + 'px';

                    // Drop the original Flash host so its fallback content cannot show
                    // through behind our stage. As before, remove the whole <object> when
                    // the match was its inner <embed>.
                    var host = el.parentNode;
                    if (host && host.nodeName === 'OBJECT' && host.parentNode) {
                        host.parentNode.removeChild(host);
                    } else if (el.parentNode) {
                        el.parentNode.removeChild(el);
                    }

                    // Attach to our own stage FIRST: load() ignores a disconnected element.
                    var stage = createOwnStage(rect);
                    stage.appendChild(player);

                    var opts = { url: proxyUrl };
                    if (Object.keys(fv).length) opts.parameters = fv;
                    player.load(opts).catch(function(err) {
                        console.error('[Ruffle] load failed:', err);
                    });
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
