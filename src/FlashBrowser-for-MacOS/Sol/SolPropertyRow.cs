using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FlashBrowserForMacOS.Models;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// P1.4 表格视图的行模型：把 <see cref="SolFile"/> 的一个属性映射成
/// 「名字 / 类型 / 值」三列，并把编辑写回<b>原值对象</b>。
///
/// <para>写回必须是原地修改（<c>value.Value = …</c>）而不是替换对象：
/// AMF3 的字符串/对象/日期引用形态（<c>ReferenceIndex</c>）都挂在原对象上，
/// 换成新对象就丢掉了「原文这一处是引用」的事实，写回的字节随之改变。
/// 编辑标量值不影响引用结构本身，是安全的；容器类值（对象/数组/Vector 等）
/// 在 P1.4 一律只读。</para>
///
/// <para>本类不含任何 UI 依赖，可直接单元测试（见
/// <c>FlashBrowserForMacOS.Tests/SolPropertyRowTests.cs</c>）。</para>
/// </summary>
public sealed class SolPropertyRow
{
    private enum RowKind { Boolean, Text, Number, Integer, ReadOnly }

    private readonly AmfValue _source;
    private readonly RowKind _kind;
    private readonly string _originalText;
    private readonly bool _originalBool;

    private SolPropertyRow(string name, AmfValue source, RowKind kind)
    {
        Name = name;
        _source = source;
        _kind = kind;
        TypeName = TypeNameOf(source);
        BoolValue = source is Amf0Boolean a0b && a0b.Value
                 || source is Amf3Boolean a3b && a3b.Value;
        TextValue = InitialText(source);
        _originalText = TextValue;
        _originalBool = BoolValue;
        DisplayValue = source.ToString() ?? string.Empty;
    }

    /// <summary>属性名（原样保留，允许重名）。</summary>
    public string Name { get; }

    /// <summary>给表格看的类型名；引用形态用后缀标出（如 <c>String @str(2)</c>）。</summary>
    public string TypeName { get; }

    /// <summary>只读行的展示文本（复杂值的摘要，可能很长，交给 UI 截断）。</summary>
    public string DisplayValue { get; }

    /// <summary>布尔行的当前值（仅 <see cref="IsBoolean"/> 行有意义）。</summary>
    public bool BoolValue { get; set; }

    /// <summary>可编辑文本行的当前值；数值行也用它承载待解析文本（InvariantCulture）。</summary>
    public string TextValue { get; set; } = string.Empty;

    public bool IsBoolean => _kind == RowKind.Boolean;
    public bool IsTextBox => _kind is RowKind.Text or RowKind.Number or RowKind.Integer;
    public bool IsDisplayOnly => _kind == RowKind.ReadOnly;

    /// <summary>编辑过的行在保存时才需要 <see cref="TryApply"/>。</summary>
    public bool IsEdited =>
        _kind == RowKind.Boolean
            ? BoolValue != _originalBool
            : !string.Equals(TextValue, _originalText, StringComparison.Ordinal);

    /// <summary>
    /// 把这一行的编辑解析并写回原值对象。解析失败时返回 <see langword="false"/> 并给出
    /// 面向用户的错误说明，<b>原值保持不变</b> —— 不做静默截断或类型替换。
    /// </summary>
    public bool TryApply([NotNullWhen(false)] out string? error)
    {
        switch (_kind)
        {
            case RowKind.Boolean:
                SetBool(BoolValue);
                error = null;
                return true;

            case RowKind.Integer:
            {
                if (!int.TryParse(TextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    || i < Amf3Integer.MinValue || i > Amf3Integer.MaxValue)
                {
                    error = $"'{Name}' 需要 {Amf3Integer.MinValue}..{Amf3Integer.MaxValue} 范围内的整数（AMF3 Integer 是 29 位）。";
                    return false;
                }

                ((Amf3Integer)_source).Value = i;
                error = null;
                return true;
            }

            case RowKind.Number:
            {
                if (!double.TryParse(TextValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    error = $"'{Name}' 不是合法数字（用 InvariantCulture 小数点，如 3.14）。";
                    return false;
                }

                SetDouble(d);
                error = null;
                return true;
            }

            case RowKind.Text:
                SetText(TextValue);
                error = null;
                return true;

            default:
                // 只读行没有可应用的东西；不该被调用，但也不算错误。
                error = null;
                return true;
        }
    }

    private void SetBool(bool value)
    {
        switch (_source)
        {
            case Amf0Boolean b: b.Value = value; break;
            case Amf3Boolean b: b.Value = value; break;
        }
    }

    private void SetText(string value)
    {
        switch (_source)
        {
            case Amf0String s: s.Value = value; break;
            case Amf0LongString s: s.Value = value; break;
            case Amf0XmlDocument s: s.Value = value; break;
            case Amf3String s: s.Value = value; break;
            case Amf3XmlDocument s: s.Value = value; break;
            case Amf3Xml s: s.Value = value; break;
        }
    }

    private void SetDouble(double value)
    {
        switch (_source)
        {
            case Amf0Number n: n.Value = value; break;
            case Amf3Double n: n.Value = value; break;
            case Amf0Date d: d.Milliseconds = value; break;
            case Amf3Date d: d.Milliseconds = value; break;
        }
    }

    private static string InitialText(AmfValue v) => v switch
    {
        Amf0Number n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        Amf3Integer i => i.Value.ToString(CultureInfo.InvariantCulture),
        Amf3Double d => d.Value.ToString("R", CultureInfo.InvariantCulture),
        Amf0Date d => d.Milliseconds.ToString("R", CultureInfo.InvariantCulture),
        Amf3Date d => d.Milliseconds.ToString("R", CultureInfo.InvariantCulture),
        Amf0String s => s.Value,
        Amf0LongString s => s.Value,
        Amf0XmlDocument s => s.Value,
        Amf3String s => s.Value,
        Amf3XmlDocument s => s.Value,
        Amf3Xml s => s.Value,
        _ => string.Empty,
    };

    /// <summary>显式手写类型名，而不是用 <c>Type.ToString()</c>：
    /// AMF3 布尔的标记由值算出（会显示成 True/False），引用形态的标记又彼此重名。</summary>
    private static string TypeNameOf(AmfValue v) => v switch
    {
        Amf0Number => "Number",
        Amf0Boolean => "Boolean",
        Amf0String => "String",
        Amf0LongString => "LongString",
        Amf0XmlDocument => "XMLDocument",
        Amf0Null => "Null",
        Amf0Undefined => "Undefined",
        Amf0Reference r => $"Reference({r.Index})",
        Amf0Date => "Date",
        Amf0StrictArray => "StrictArray",
        Amf0TypedObject t => $"TypedObject '{t.ClassName}'",
        Amf0EcmaArray => "EcmaArray",
        Amf0Object => "Object",

        Amf3Boolean => "Boolean",
        Amf3Integer => "Integer",
        Amf3Double => "Double",
        Amf3String s => s.ReferenceIndex is { } i ? $"String @str({i})" : "String",
        Amf3Date d => d.ReferenceIndex is { } i ? $"Date @ref({i})" : "Date",
        Amf3XmlDocument => "XMLDocument",
        Amf3Xml => "XML",
        Amf3ByteArray => "ByteArray",
        Amf3Array => "Array",
        Amf3Object => "Object",
        Amf3ObjectReference r => $"ObjectReference({r.Index})",
        Amf3VectorInt => "Vector<int>",
        Amf3VectorUint => "Vector<uint>",
        Amf3VectorDouble => "Vector<Number>",
        Amf3VectorObject => "Vector<Object>",
        Amf3Dictionary => "Dictionary",
        Amf3Undefined => "Undefined",
        Amf3Null => "Null",

        _ => v.GetType().Name,
    };

    /// <summary>为整个文件建行，顺序与 <see cref="SolFile.Properties"/> 一致。</summary>
    public static IReadOnlyList<SolPropertyRow> FromFile(SolFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var rows = new List<SolPropertyRow>(file.Properties.Count);
        foreach (var (name, value) in file.Properties)
        {
            rows.Add(For(name, value));
        }

        return rows;
    }

    private static SolPropertyRow For(string name, AmfValue value) => value switch
    {
        Amf0Boolean or Amf3Boolean => new SolPropertyRow(name, value, RowKind.Boolean),
        Amf0String or Amf0LongString or Amf0XmlDocument
            or Amf3String or Amf3XmlDocument or Amf3Xml => new SolPropertyRow(name, value, RowKind.Text),
        Amf3Integer => new SolPropertyRow(name, value, RowKind.Integer),
        Amf0Number or Amf3Double or Amf0Date or Amf3Date => new SolPropertyRow(name, value, RowKind.Number),
        _ => new SolPropertyRow(name, value, RowKind.ReadOnly),
    };
}
