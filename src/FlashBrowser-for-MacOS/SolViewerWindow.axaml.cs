using System.Globalization;
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

    public SolViewerWindow()
    {
        InitializeComponent();
    }

    /// <summary>命令行/外部直接指定路径时使用：打开窗口的同时加载文件。</summary>
    public SolViewerWindow(string path) : this()
    {
        OpenPath(path);
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

    private void BindFile(SolFile sol, string path)
    {
        _sol = sol;
        _path = path;
        _rows = SolPropertyRow.FromFile(sol);
        Grid.ItemsSource = _rows;
        FilePathText.Text = path;
        InfoText.Text =
            $"{sol.Name} · AMF 版本 0x{(byte)sol.Format:x2} ({sol.Format}) · {sol.Properties.Count} 个属性";
        Title = $"SOL 存档 — {Path.GetFileName(path)}";
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
