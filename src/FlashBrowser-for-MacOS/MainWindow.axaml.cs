using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FlashBrowserForMacOS.Ruffle;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Events;

namespace FlashBrowserForMacOS;

public partial class MainWindow : Window
{
    /// <summary>
    /// 4399.com is the project's main use case (running Flash mini-games).
    /// </summary>
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

        UpdateStatus($"Loaded: {HomeUrl}");
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
