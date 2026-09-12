using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlashBrowserForMacOS.Ruffle;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Events;

namespace FlashBrowserForMacOS;

public partial class MainWindow : Window
{
    // [DEV ONLY] These must be rooted. DispatcherTimer.RunOnce returns IDisposable and
    // nothing else holds it, so dropping the value lets the GC collect a still-pending
    // timer — which then never fires, a failure mode indistinguishable from "the
    // dispatcher is wedged". Holding them in fields is what keeps that ambiguity out.
    private IDisposable? _diagSentinel;
    private IDisposable? _diagPumpStart;
    private DispatcherTimer? _diagPump;
    private System.Threading.Timer? _diagProbe;
    private int _diagPumpTicks;

    /// <summary>[DEV ONLY] Seconds to wait before the hand-driven pump starts (see
    /// <see cref="WirePumpProbe"/>); the wait is what makes the run a self-contained A/B.</summary>
    private const int PumpProbeAfterSeconds = 8;
    private const string HomeUrl = "https://www.4399.com/";

    private AvaloniaCefBrowser _browser = null!;

    // The Avalonia 11 XAML compiler auto-generates InitializeComponent().
    // We only put custom wiring AFTER that call from the constructor.
    public MainWindow()
    {
        // Source-gen InitializeComponent: loads the AXAML and resolves
        // x:Name fields like AddressTextBox, BackButton, etc.
        InitializeComponent();

        // Build the CEF browser control and mount it on the XAML placeholder.
        _browser = new AvaloniaCefBrowser
        {
            Address = HomeUrl
        };
        _browser.LoadStart    += OnBrowserLoadStart;
        _browser.LoadEnd      += OnBrowserLoadEnd;
        _browser.TitleChanged += OnBrowserTitleChanged;
        _browser.JavascriptContextCreated += OnJavascriptContextCreated;
        BrowserWrapper.Child  = _browser;

        if (Diagnostics.Enabled)
        {
            // [DEV ONLY] Mirrors what the GUI is doing into FB_DIAG_LOG. Wired only when
            // that variable is set, so a normal run pays nothing for it. BrowserInitialized
            // is the decisive one: if it never fires, no CEF browser was ever created and
            // no amount of page-level debugging can help.
            Diagnostics.Log($"--- diag start; home={HomeUrl} ---");
            _browser.BrowserInitialized += () => Diagnostics.Log("browser initialized");

            // Split "the control never got laid out" from "the control was laid out but CEF
            // never got pumped". CefGlue creates the CEF browser from the control's
            // SizeChanged, so a laid-out control with no "browser initialized" line points
            // at the external message pump (CefSettings.ExternalMessagePump = true on macOS).
            var layoutLogged = false;
            _browser.LayoutUpdated += (_, _) =>
            {
                if (layoutLogged)
                {
                    return;
                }

                layoutLogged = true;
                Diagnostics.Log(
                    $"control laid out: {_browser.Bounds.Width}x{_browser.Bounds.Height} " +
                    $"visible={_browser.IsVisible} attached={_browser.IsAttachedToVisualTree()}");
            };

            Opened += (_, _) => Diagnostics.Log($"window opened: {Width}x{Height}");

            // Sentinel: makes the log self-contained when the browser never initialises.
            _diagSentinel = DispatcherTimer.RunOnce(
                () => Diagnostics.Log(
                    $"8s check: attached={_browser.IsAttachedToVisualTree()} " +
                    $"visible={_browser.IsVisible} bounds={_browser.Bounds.Width}x{_browser.Bounds.Height}"),
                TimeSpan.FromSeconds(8));

            WirePumpProbe();
            _browser.AddressChanged += (_, url) => Diagnostics.Log($"address changed: {url}");
            _browser.LoadStart += (_, e) =>
                Diagnostics.Log($"load start: main={e.Frame.IsMain} url={e.Frame.Url}");
            _browser.LoadEnd += (_, e) =>
                Diagnostics.Log($"load end:   main={e.Frame.IsMain} http={e.HttpStatusCode} url={e.Frame.Url}");
            _browser.LoadError += (_, e) =>
                Diagnostics.Log($"LOAD ERROR: {e.ErrorCode} '{e.ErrorText}' url={e.FailedUrl}");
            _browser.ConsoleMessage += (_, e) =>
                Diagnostics.Log($"console[{e.Level}] {e.Message}  ({e.Source}:{e.Line})");
        }

        UpdateStatus($"Loaded: {HomeUrl}");
    }

    /// <summary>
    /// [DEV ONLY] The P0-0 probe. It separates the two explanations that survive everything
    /// else having been ruled out — see README「P0-0」.
    ///
    /// <para><b>(a) A background probe.</b> A <see cref="System.Threading.Timer"/> callback
    /// does not need the Avalonia dispatcher, so it keeps answering while the UI thread is
    /// busy or wedged. If these lines are present but nothing else moves, the dispatcher is
    /// the problem; if they stop altogether, the process is gone instead.</para>
    ///
    /// <para><b>(b) A hand-driven message pump.</b> CEF's browser creation is an asynchronous
    /// UI-thread task, so it completes only while somebody calls
    /// <c>CefRuntime.DoMessageLoopWork()</c>. macOS forces <c>ExternalMessagePump = true</c>,
    /// which makes CefGlue's own pump — <c>AvaloniaBrowserProcessHandler
    /// .OnScheduleMessagePumpWork</c> → <c>Observable.Interval</c> →
    /// <c>AvaloniaScheduler.Instance</c> — the only thing driving it. If that chain never
    /// delivers, the posted task never runs: no <c>OnAfterCreated</c>, no profile, no cache
    /// directory, no navigation. Starting the pump by hand after
    /// <see cref="PumpProbeAfterSeconds"/> seconds turns that into a within-run A/B: the
    /// probe lines before the switch and after it are the whole experiment.</para>
    ///
    /// <para>Note that this does <b>not</b> suspect a wedged dispatcher for the missing
    /// <c>BrowserInitialized</c>: <c>CommonBrowserAdapter.OnBrowserCreated</c> raises it
    /// synchronously from CEF's <c>OnAfterCreated</c> callback, with no dispatcher hop.</para>
    /// </summary>
    private void WirePumpProbe()
    {
        Diagnostics.Log(
            $"cef runtime: initialized={CefRuntime.IsInitialized} platform={CefRuntime.Platform} " +
            $"chrome={CefRuntime.ChromeVersion}");

        _diagProbe = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    Diagnostics.Log(
                        $"probe: browserInitialized={_browser.IsBrowserInitialized} " +
                        $"attached={_browser.IsAttachedToVisualTree()} " +
                        $"bounds={_browser.Bounds.Width}x{_browser.Bounds.Height}");
                }
                catch (Exception ex)
                {
                    // Reading Avalonia state off-thread is not guaranteed; a throw here must
                    // not take the process down and masquerade as the bug being measured.
                    Diagnostics.Log($"probe threw: {ex.GetType().Name}: {ex.Message}");
                }
            },
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));

        if (!Diagnostics.PumpProbe)
        {
            Diagnostics.Log("manual pump: not enabled (set FB_DIAG_PUMP=1 to run it)");
            return;
        }

        _diagPumpStart = DispatcherTimer.RunOnce(
            () =>
            {
                Diagnostics.Log("manual pump: starting");

                _diagPump = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(16),
                    DispatcherPriority.Background,
                    (_, _) =>
                    {
                        _diagPumpTicks++;

                        try
                        {
                            CefRuntime.DoMessageLoopWork();
                        }
                        catch (Exception ex)
                        {
                            Diagnostics.Log($"manual pump threw: {ex.GetType().Name}: {ex.Message}");
                            _diagPump!.Stop();
                            return;
                        }

                        if (_diagPumpTicks == 1 || _diagPumpTicks % 60 == 0)
                        {
                            Diagnostics.Log(
                                $"manual pump: {_diagPumpTicks} ticks, " +
                                $"browserInitialized={_browser.IsBrowserInitialized}");
                        }
                    });

                _diagPump.Start();
            },
            TimeSpan.FromSeconds(PumpProbeAfterSeconds));
    }

    // ---- Navigation button handlers (XAML-bound, so signature must match) ----

    private void OnBackClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browser.GoBack();

    private void OnForwardClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browser.GoForward();

    private void OnReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browser.Reload();

    private void OnHomeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browser.Address = HomeUrl;

    private void OnGoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => NavigateFromAddressBar();

    private void OnDevToolsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browser.ShowDeveloperTools();

    private void OnSolViewerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new SolViewerWindow().Show();

    // ---- Address bar ----

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBar();
        }
    }

    private void NavigateFromAddressBar()
    {
        var raw = AddressTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        // Naive URL normalization: "4399.com" -> "https://4399.com"
        if (!raw.Contains("://") && !raw.StartsWith("about:"))
        {
            raw = "https://" + raw;
        }
        _browser.Address = raw;
    }

    // ---- Browser event handlers ----

    private void OnBrowserLoadStart(object? sender, LoadStartEventArgs e)
    {
        if (!e.Frame.IsMain)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AddressTextBox.Text = e.Frame.Url;
            UpdateStatus($"Loading: {e.Frame.Url}");
        });
    }

    private void OnBrowserLoadEnd(object? sender, LoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => UpdateStatus($"Done: {e.Frame.Url}"));

        // Phase 2: inject Ruffle into 4399 Flash game pages so the legacy
        // <object>/<embed> SWF tags get polyfilled and playable in-browser.
        if (RuffleInjector.ShouldInject(e.Frame.Url))
        {
            _browser.ExecuteJavaScript(RuffleInjector.BuildInjectionScript());
        }
    }

    private void OnJavascriptContextCreated(object? sender, JavascriptContextLifetimeEventArgs e)
    {
        if (!e.Frame.IsMain)
        {
            return;
        }

        // Inject the Flash-presence spoof before the page's own scripts run, so 4399's
        // flashopen1.js detection (hasUsableFlash) passes and never disrupts the player.
        if (RuffleInjector.ShouldInject(e.Frame.Url) || RuffleInjector.ShouldInject(_browser.Address))
        {
            _browser.ExecuteJavaScript(RuffleInjector.BuildEarlyInjectionScript());
        }
    }

    private void OnBrowserTitleChanged(object? sender, string title)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(title))
            {
                Title = title;
            }
        });
    }

    private void UpdateStatus(string text) => StatusText.Text = text;
}
