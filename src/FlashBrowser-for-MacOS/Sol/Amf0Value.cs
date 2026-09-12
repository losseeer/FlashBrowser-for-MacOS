using System.Globalization;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// 一个 AMF0 值。
///
/// 类型模型刻意做成「一型一类」而不是单一 payload 字段：round-trip 的目标是**字节级还原**，
/// 而 AMF0 里语义相近的值有不同的字节形态（<c>String</c> 与 <c>LongString</c>、
/// <c>Object</c> / <c>TypedObject</c> / <c>EcmaArray</c>）。若在解析时把它们归并成一个
/// 「字典」，写回时就无法还原，round-trip 必然出现非零误差。
/// </summary>
public abstract class Amf0Value : AmfValue
{
    /// <summary>本值在 AMF0 里的类型标记。</summary>
    public abstract Amf0Type Type { get; }
}

/// <summary>AMF0 <c>Number</c>（<c>0x00</c>）：8 字节大端 IEEE-754 双精度。</summary>
public sealed class Amf0Number : Amf0Value
{
    public Amf0Number(double value) => Value = value;

    public double Value { get; set; }

    public override Amf0Type Type => Amf0Type.Number;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>AMF0 <c>Boolean</c>（<c>0x01</c>）：1 字节，非零为真。</summary>
public sealed class Amf0Boolean : Amf0Value
{
    public Amf0Boolean(bool value) => Value = value;

    public bool Value { get; set; }

    public override Amf0Type Type => Amf0Type.Boolean;

    public override string ToString() => Value ? "true" : "false";
}

/// <summary>AMF0 <c>String</c>（<c>0x02</c>）：<c>u16</c> 长度 + UTF-8 字节。</summary>
public sealed class Amf0String : Amf0Value
{
    public Amf0String(string value) => Value = value;

    public string Value { get; set; }

    public override Amf0Type Type => Amf0Type.String;

    public override string ToString() => Value;
}

/// <summary>AMF0 <c>LongString</c>（<c>0x0C</c>）：<c>u32</c> 长度 + UTF-8 字节。
/// 与 <see cref="Amf0String"/> 语义相同、字节形态不同，因此单独占一个类型以保真。</summary>
public sealed class Amf0LongString : Amf0Value
{
    public Amf0LongString(string value) => Value = value;

    public string Value { get; set; }

    public override Amf0Type Type => Amf0Type.LongString;

    public override string ToString() => Value;
}

/// <summary>AMF0 <c>XMLDocument</c>（<c>0x0F</c>）：<c>u32</c> 长度 + UTF-8 字节（与长字符串同形）。</summary>
public sealed class Amf0XmlDocument : Amf0Value
{
    public Amf0XmlDocument(string value) => Value = value;

    public string Value { get; set; }

    public override Amf0Type Type => Amf0Type.XmlDocument;

    public override string ToString() => Value;
}

/// <summary>AMF0 <c>Null</c>（<c>0x05</c>）：无 payload。</summary>
public sealed class Amf0Null : Amf0Value
{
    public override Amf0Type Type => Amf0Type.Null;

    public override string ToString() => "null";
}

/// <summary>AMF0 <c>Undefined</c>（<c>0x06</c>）：无 payload。</summary>
public sealed class Amf0Undefined : Amf0Value
{
    public override Amf0Type Type => Amf0Type.Undefined;

    public override string ToString() => "undefined";
}

/// <summary>AMF0 <c>Reference</c>（<c>0x07</c>）：<c>u16</c> 下标，指向同一 AMF0 图里此前出现过的对象。
/// 本读取器按原样保留下标，不尝试重建引用关系（重建需要值图的构建顺序，属于 P1.4 编辑功能的范围）。</summary>
public sealed class Amf0Reference : Amf0Value
{
    public Amf0Reference(ushort index) => Index = index;

    public ushort Index { get; set; }

    public override Amf0Type Type => Amf0Type.Reference;

    public override string ToString() => $"@ref({Index})";
}

/// <summary>AMF0 <c>Date</c>（<c>0x0B</c>）：8 字节双精度毫秒 + <c>i16</c> 时区分钟数。</summary>
public sealed class Amf0Date : Amf0Value
{
    public Amf0Date(double milliseconds, short timeZoneMinutes)
    {
        Milliseconds = milliseconds;
        TimeZoneMinutes = timeZoneMinutes;
    }

    public double Milliseconds { get; set; }

    /// <summary>规范定义为「本地时间与 UTC 的偏移分钟数」。实测样本里常见 <c>0</c>。</summary>
    public short TimeZoneMinutes { get; set; }

    public override Amf0Type Type => Amf0Type.Date;

    public override string ToString() =>
        $"{Milliseconds.ToString(CultureInfo.InvariantCulture)}ms tz={TimeZoneMinutes}";
}

/// <summary>AMF0 <c>StrictArray</c>（<c>0x0A</c>）：<c>u32</c> 元素个数 + 各元素（紧凑、按序、无键名）。</summary>
public sealed class Amf0StrictArray : Amf0Value
{
    public List<Amf0Value> Items { get; } = new();

    public override Amf0Type Type => Amf0Type.StrictArray;

    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

/// <summary>
/// 成员表类值（<c>Object</c> / <c>TypedObject</c> / <c>EcmaArray</c>）的公共基类。
///
/// 成员用 <see cref="List{T}"/> 而非 <see cref="Dictionary{TKey,TValue}"/> 保存，因为 AMF0 是
/// 有序序列，写回时必须按原顺序输出才能字节级还原，且 Flash 写出的键名允许重复。
/// </summary>
public abstract class Amf0ObjectBase : Amf0Value
{
    public List<KeyValuePair<string, Amf0Value>> Members { get; } = new();

    /// <summary>按名取成员；没有该名时返回 <see langword="null"/>。有重名时返回第一个。</summary>
    public Amf0Value? TryGet(string name)
    {
        foreach (var (key, value) in Members)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>按名覆盖已有成员，不存在则追加到末尾。</summary>
    public void Set(string name, Amf0Value value)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (string.Equals(Members[i].Key, name, StringComparison.Ordinal))
            {
                Members[i] = new KeyValuePair<string, Amf0Value>(name, value);
                return;
            }
        }

        Members.Add(new KeyValuePair<string, Amf0Value>(name, value));
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        for (var i = 0; i < Members.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Members[i].Key).Append(": ").Append(Members[i].Value);
        }

        builder.Append('}');
        return builder.ToString();
    }
}

/// <summary>AMF0 <c>Object</c>（<c>0x03</c>）：匿名成员表，以 <c>00 00 09</c> 结束。匿名对象的 class 名是空串，
/// 与 <see cref="Amf0TypedObject"/> 是同一种布局、不同形态，故分开建模。</summary>
public sealed class Amf0Object : Amf0ObjectBase
{
    public override Amf0Type Type => Amf0Type.Object;
}

/// <summary>AMF0 <c>TypedObject</c>（<c>0x10</c>）：先写 class 名（<c>u16</c> 长度 + UTF-8），再写成员表。</summary>
public sealed class Amf0TypedObject : Amf0ObjectBase
{
    public Amf0TypedObject(string className) => ClassName = className;

    public string ClassName { get; set; }

    public override Amf0Type Type => Amf0Type.TypedObject;

    public override string ToString() => $"{ClassName} {base.ToString()}";
}

/// <summary>
/// AMF0 <c>ECMAArray</c>（<c>0x08</c>）：<c>u32</c> 声明个数 + 成员表 + <c>00 00 09</c>。
///
/// <see cref="DeclaredCount"/> 按原样保留而不是用 <see cref="Amf0ObjectBase.Members"/> 的数量覆盖 ——
/// 规范允许两者不一致，写回时若改成实际数量，round-trip 就不再字节级相等。
/// </summary>
public sealed class Amf0EcmaArray : Amf0ObjectBase
{
    public Amf0EcmaArray(uint declaredCount = 0) => DeclaredCount = declaredCount;

    public uint DeclaredCount { get; set; }

    public override Amf0Type Type => Amf0Type.EcmaArray;

    public override string ToString() => $"{base.ToString()} (declared {DeclaredCount})";
}
