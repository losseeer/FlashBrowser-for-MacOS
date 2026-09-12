using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FlashBrowserForMacOS.Models;
using FlashBrowserForMacOS.Sol;

namespace FlashBrowserForMacOS;

/// <summary>
/// P1.4 的 .sol 存档查看/编辑窗口。
///
/// <para>编辑模型：表格行的编辑先进 <see cref="SolPropertyRow"/>（见其类型注释里
/// 「原地修改」的保真约束），点保存时逐行 <see cref="SolPropertyRow.TryApply"/>
/// 解析回类型化值 —— 有任何一行解析失败就<b>整次中止保存</b>并在状态栏列出错误，
/// 不做静默降级（对齐项目口径「不许把失败伪装成结果」）。</para>
/// </summary>
public partial class SolViewerWindow : Window
{
    private static readonly FilePickerFileType SolFileType = new("Flash 存档 (.sol)")
    {
        Patterns = ["*.sol"],
        MimeTypes = ["application/octet-stream"],
    };

    private SolFile? _sol;
    private string? _path;
    private IReadOnlyList<SolPropertyRow> _rows = [];
    private BrowserSaveBridge? _saveBridge;
    private List<PageSave> _pageSaves = [];

    /// <summary>页面存档列表条目（localStorage 键 → Ruffle 存档）。</summary>
    private sealed record PageSave(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] int Size)
    {
        public override string ToString() => $"{Name}  ({Size} B)";
    }

    public SolViewerWindow()
    {
        InitializeComponent();
    }

    /// <summary>命令行/外部直接指定路径时使用：打开窗口的同时加载文件。</summary>
    public SolViewerWindow(string path) : this()
    {
        OpenPath(path);
    }

    /// <summary>接入浏览器侧的页面存档（P1.5）。传入后显示「页面存档」工具行。</summary>
    public SolViewerWindow(BrowserSaveBridge? saveBridge) : this()
    {
        _saveBridge = saveBridge;
        SavePanel.IsVisible = saveBridge is not null;
    }

    // ---- Loading ----

    /// <summary>加载一个 .sol 文件。失败时在状态栏报错并保留当前内容。</summary>
    public void OpenPath(string path)
    {
        try
        {
            var sol = SolFile.ReadFile(path);
            BindFile(sol, path);
            SetStatus($"已加载 {sol.Properties.Count} 个属性。");
            Diagnostics.Log(
                $"sol viewer: loaded '{sol.Name}' format=0x{(byte)sol.Format:x2} " +
                $"properties={sol.Properties.Count} path={path}");
        }
        catch (Exception ex)
        {
            // Explicit failure: an unparsable file must never look like "loaded, empty".
            SetStatus($"打开失败：{ex.Message}");
        }
    }

    private void BindFile(SolFile sol, string path) =>
        BindCore(sol, path, path, $"SOL 存档 — {Path.GetFileName(path)}");

    /// <summary>把一个（可能来自页面、还没有磁盘路径的）文件绑进表格。</summary>
    private void BindCore(SolFile sol, string? path, string pathDisplay, string title)
    {
        _sol = sol;
        _path = path;
        _rows = SolPropertyRow.FromFile(sol);
        Grid.ItemsSource = _rows;
        FilePathText.Text = pathDisplay;
        InfoText.Text =
            $"{sol.Name} · AMF 版本 0x{(byte)sol.Format:x2} ({sol.Format}) · {sol.Properties.Count} 个属性";
        Title = title;
    }

    // ---- Toolbar handlers ----

    private async void OnOpenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 Flash 存档",
            AllowMultiple = false,
            FileTypeFilter = [SolFileType],
        });

        if (files.Count > 0)
        {
            OpenPath(files[0].Path.LocalPath);
        }
    }

    private void OnReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_path is null)
        {
            return;
        }

        var hadEdits = _rows.Any(r => r.IsEdited);
        OpenPath(_path);

        if (hadEdits)
        {
            SetStatus("已重新加载；未保存的修改已丢弃。");
        }
    }

    private void OnSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sol is null)
        {
            return;
        }

        if (!ApplyEdits(out var errors))
        {
            SetStatus("保存中止（有属性无法解析）：" + string.Join("；", errors));
            return;
        }

        if (_path is null)
        {
            _ = SaveAsAsync();
            return;
        }

        WriteBytes(_path);
    }

    private async void OnSaveAsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sol is null)
        {
            return;
        }

        await SaveAsAsync();
    }

    // ---- Page-save handlers (P1.5, enabled by BrowserSaveBridge) ----

    private async void OnLoadPageSavesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_saveBridge is null)
        {
            return;
        }

        try
        {
            SetStatus("正在读取页面存档…");
            var json = await _saveBridge.ListSavesAsync();
            _pageSaves = JsonSerializer.Deserialize<List<PageSave>>(json ?? "[]") ?? [];
            SavesCombo.ItemsSource = _pageSaves;
            SavesCombo.SelectedIndex = _pageSaves.Count > 0 ? 0 : -1;
            SetStatus(_pageSaves.Count > 0
                ? $"读到 {_pageSaves.Count} 个页面存档。"
                : "页面里没有 Ruffle 存档（先玩一下游戏并触发存档）。");
            Diagnostics.Log($"sol viewer: page saves listed count={_pageSaves.Count}");
        }
        catch (Exception ex)
        {
            SetStatus($"读取页面存档失败：{ex.Message}");
        }
    }

    private async void OnExportSelectedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_saveBridge is null)
        {
            return;
        }

        if (SavesCombo.SelectedItem is not PageSave save)
        {
            SetStatus("先「读取页面存档」并选择一条。");
            return;
        }

        try
        {
            var base64 = await _saveBridge.FetchSaveAsync(save.Key);
            if (string.IsNullOrEmpty(base64))
            {
                SetStatus("导出失败：该存档内容为空（可能已被页面删除）。");
                return;
            }

            // The value is base64 of a standard .sol byte stream — the parser takes it as is.
            var sol = SolFile.Read(Convert.FromBase64String(base64));
            BindCore(sol, path: null, pathDisplay: $"（页面存档 {save.Key}）", title: $"SOL 存档 — {sol.Name}（页面存档）");
            SetStatus($"已载入 {sol.Properties.Count} 个属性；编辑后用「另存为…」保存为 .sol 文件。");
            Diagnostics.Log($"sol viewer: exported from page key={save.Key} properties={sol.Properties.Count}");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}");
        }
    }

    private async void OnImportSolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_saveBridge is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 Flash 存档到页面",
            AllowMultiple = false,
            FileTypeFilter = [SolFileType],
        });

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(files[0].Path.LocalPath);

            // Parse BEFORE writing anything into the page: a broken file must not
            // replace a working save. Ruffle would decode it and choke on garbage.
            var sol = SolFile.Read(bytes);
            var base64 = Convert.ToBase64String(bytes);

            // Target key: an existing page save with the same name wins (it carries
            // the game's real localPath/host, which we cannot reconstruct better);
            // otherwise derive a fresh key from the movie URL.
            var key = _pageSaves.FirstOrDefault(s => s.Name == sol.Name)?.Key;
            if (key is null)
            {
                var derived = await _saveBridge.DeriveKeyAsync(sol.Name);
                if (string.IsNullOrEmpty(derived))
                {
                    SetStatus("导入失败：页面里没有可派生存档键的 Ruffle 播放器（先打开游戏页）。");
                    return;
                }

                key = derived;
            }

            await _saveBridge.PutSaveAsync(key, base64);
            SetStatus($"已写入 {key}；刷新页面后生效。");
            Diagnostics.Log($"sol viewer: imported to page key={key} properties={sol.Properties.Count}");
        }
        catch (Exception ex)
        {
            SetStatus($"导入失败：{ex.Message}");
        }
    }

    private async Task SaveAsAsync()
    {
        if (_sol is null)
        {
            return;
        }

        var suggestedName = _path is not null
            ? Path.GetFileName(_path)
            : $"{_sol.Name}.sol";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存 Flash 存档",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sol",
            FileTypeChoices = [SolFileType],
        });

        if (file is null)
        {
            SetStatus("已取消保存。");
            return;
        }

        _path = file.Path.LocalPath;
        WriteBytes(_path);
    }

    // ---- Write path ----

    /// <summary>把所有编辑过的行解析回类型化值。任何一行失败即整体失败。</summary>
    private bool ApplyEdits(out List<string> errors)
    {
        errors = [];

        foreach (var row in _rows)
        {
            if (row.IsEdited && !row.TryApply(out var error))
            {
                errors.Add(error);
            }
        }

        return errors.Count == 0;
    }

    private void WriteBytes(string path)
    {
        try
        {
            File.WriteAllBytes(path, _sol!.Write());
            SetStatus($"已保存 {path}");
            Diagnostics.Log($"sol viewer: saved properties={_sol.Properties.Count} path={path}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
