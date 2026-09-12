using System.Globalization;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// 一个 AMF3 值。
///
/// 与 <see cref="Amf0Value"/> 同样的取舍 —— 一型一类，不为「好用的统一接口」牺牲字节保真。
/// AMF3 比 AMF0 更容易在归并时丢信息，原因有三：
/// <list type="number">
/// <item><c>False</c>/<c>True</c> 是独立标记而不是布尔的 payload；<c>Integer</c>/<c>Double</c>
/// 数值上重叠但字节形态不同。</item>
/// <item>数组、对象、Vector 都参与**对象引用表**，第二次出现可能只有一个下标。</item>
/// <item>Vector 的基础类型元素是**定长原始值**（4 / 8 字节），不是 AMF3 值，套用 AMF3 值模型会改变字节。</item>
/// </list>
///
/// <para><b>引用形态的保真</b>：每个可被引用的值都带一个 <see cref="Amf3String.ReferenceIndex"/> 一类的下标字段。
/// 非 <see langword="null"/> 表示「这一处在原文里就是引用」，写出时按引用写、不再展开。
/// 这样 <c>读 → 写</c> 的字节还原**不依赖于我们猜对 Flash 的去重策略** —— 即使猜错，
/// 我们写出的引用下标仍然是原文件里那个下标。</para>
/// </summary>
public abstract class Amf3Value : AmfValue
{
    /// <summary>本值在 AMF3 里的类型标记。</summary>
    public abstract Amf3Type Type { get; }
}

/// <summary>AMF3 <c>Undefined</c>（<c>0x00</c>）：无 payload。</summary>
public sealed class Amf3Undefined : Amf3Value
{
    public override Amf3Type Type => Amf3Type.Undefined;

    public override string ToString() => "undefined";
}

/// <summary>AMF3 <c>Null</c>（<c>0x01</c>）：无 payload。</summary>
public sealed class Amf3Null : Amf3Value
{
    public override Amf3Type Type => Amf3Type.Null;

    public override string ToString() => "null";
}

/// <summary>AMF3 布尔：<c>False</c>（<c>0x02</c>）/ <c>True</c>（<c>0x03</c>），无 payload。
/// 标记由值决定，所以 <see cref="Type"/> 是算出来的。</summary>
public sealed class Amf3Boolean : Amf3Value
{
    public Amf3Boolean(bool value) => Value = value;

    public bool Value { get; set; }

    public override Amf3Type Type => Value ? Amf3Type.True : Amf3Type.False;

    public override string ToString() => Value ? "true" : "false";
}

/// <summary>AMF3 <c>Integer</c>（<c>0x04</c>）：29 位有符号整数，用 U29 编码。
/// 取值范围是 <c>-2^28 .. 2^28-1</c>；超出这个范围的 <see cref="int"/> 写出时会显式报错。</summary>
public sealed class Amf3Integer : Amf3Value
{
    public const int MinValue = -0x10000000;
    public const int MaxValue = 0x0FFFFFFF;

    public Amf3Integer(int value) => Value = value;

    public int Value { get; set; }

    public override Amf3Type Type => Amf3Type.Integer;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>AMF3 <c>Double</c>（<c>0x05</c>）：8 字节大端 IEEE-754 双精度。</summary>
public sealed class Amf3Double : Amf3Value
{
    public Amf3Double(double value) => Value = value;

    public double Value { get; set; }

    public override Amf3Type Type => Amf3Type.Double;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// AMF3 <c>String</c>（<c>0x06</c>）：U29S 编码 —— 要么是内联字面量（<c>(长度 &lt;&lt; 1) | 1</c>），
/// 要么是字符串引用表的下标（<c>下标 &lt;&lt; 1</c>）。
///
/// 这个类型也用作**成员名**的载体（对象的动态成员名是 U29S），因为成员名同样可能写成引用；
/// 容器层的属性名与 traits 里的类名 / 密封成员名则只保留 <see cref="string"/>，
/// 遇到引用形态会显式报错而不是悄悄展开（见 <see cref="Amf3Reader"/>）。
/// </summary>
public sealed class Amf3String : Amf3Value
{
    public Amf3String(string value) => Value = value;

    public string Value { get; set; }

    /// <summary>字符串引用表下标；<see langword="null"/> 表示原文里这是内联字面量。</summary>
    public int? ReferenceIndex { get; set; }

    public override Amf3Type Type => Amf3Type.String;

    public override string ToString() =>
        ReferenceIndex is { } index ? $"@str({index}) {Value}" : Value;
}

/// <summary>AMF3 <c>XMLDocument</c>（<c>0x07</c>）：文本用 U29X 形态给出，引用走的是**对象**引用表
/// （不是字符串表），文本内容本身也不进字符串表。</summary>
public sealed class Amf3XmlDocument : Amf3ReferenceableValue
{
    public Amf3XmlDocument(string value) => Value = value;

    public string Value { get; set; }

    public override Amf3Type Type => Amf3Type.XmlDocument;

    public override string ToString() => ReferenceIndex is { } i ? $"@xmlDoc({i})" : Value;
}

/// <summary>AMF3 <c>XML</c>（<c>0x0B</c>）：E4X XML。引用与入表规则同 <see cref="Amf3XmlDocument"/>。</summary>
public sealed class Amf3Xml : Amf3ReferenceableValue
{
    public Amf3Xml(string value) => Value = value;

    public string Value { get; set; }

    public override Amf3Type Type => Amf3Type.Xml;

    public override string ToString() => ReferenceIndex is { } i ? $"@xml({i})" : Value;
}

/// <summary>AMF3 <c>Date</c>（<c>0x08</c>）：8 字节大端双精度毫秒。
/// 注意与 <see cref="Amf0Date"/> 不同，AMF3 的 Date 没有时区字段。</summary>
public sealed class Amf3Date : Amf3Value
{
    public Amf3Date(double milliseconds) => Milliseconds = milliseconds;

    public double Milliseconds { get; set; }

    public int? ReferenceIndex { get; set; }

    public override Amf3Type Type => Amf3Type.Date;

    public override string ToString() =>
        ReferenceIndex is { } i
            ? $"@date({i})"
            : $"{Milliseconds.ToString(CultureInfo.InvariantCulture)}ms";
}

/// <summary>AMF3 <c>ByteArray</c>（<c>0x0C</c>）：U29 字节长度（带引用形态）+ 原始字节。</summary>
public sealed class Amf3ByteArray : Amf3Value
{
    public Amf3ByteArray(byte[] bytes) => Bytes = bytes;

    public byte[] Bytes { get; set; }

    public int? ReferenceIndex { get; set; }

    public override Amf3Type Type => Amf3Type.ByteArray;

    public override string ToString() =>
        ReferenceIndex is { } i ? $"@bytes({i})" : $"{Bytes.Length} B";
}

/// <summary>AMF3 可被对象引用表引用的值的公共基类。</summary>
public abstract class Amf3ReferenceableValue : Amf3Value
{
    /// <summary>对象引用表下标；<see langword="null"/> 表示原文里这是内联值。</summary>
    public int? ReferenceIndex { get; set; }
}

/// <summary>
/// AMF3 <c>Array</c>（<c>0x09</c>）：一个 U29 给稠密部分个数、再一个 U29 给关联部分个数。
/// 稠密部分是紧凑的按序值（无键名），关联部分是 <c>U29S 键 + 值</c>。
/// 实测样本（<c>FBCookie.sol</c>）只用了稠密部分。
/// </summary>
public sealed class Amf3Array : Amf3ReferenceableValue
{
    public List<Amf3Value> DenseItems { get; } = new();

    /// <summary>关联部分。键是 U29S，所以可能是字符串引用，用 <see cref="Amf3String"/> 保存。</summary>
    public List<KeyValuePair<Amf3String, Amf3Value>> AssociativeMembers { get; } = new();

    public override Amf3Type Type => Amf3Type.Array;

    public override string ToString() =>
        ReferenceIndex is { } i
            ? $"@array({i})"
            : $"[{string.Join(", ", DenseItems)}]{{assoc {AssociativeMembers.Count}}}";
}

/// <summary>
/// AMF3 traits（类定义）：描述一个对象有多少密封成员、是否动态、是否 externalizable。
///
/// <para>类名与密封成员名都是 U29S，因此用 <see cref="Amf3String"/> 保存 —— 它们同样可能是字符串引用，
/// 展开成字面量会让写回的字节与原文件不同。</para>
/// </summary>
public sealed class Amf3Traits
{
    public Amf3Traits(Amf3String className, bool externalizable, bool dynamic)
    {
        ClassName = className;
        Externalizable = externalizable;
        Dynamic = dynamic;
    }

    /// <summary>类名。匿名类是空串（匿名动态类是绝大多数 LSO 里的形态）。</summary>
    public Amf3String ClassName { get; set; }

    public bool Externalizable { get; set; }

    public bool Dynamic { get; set; }

    /// <summary>密封成员的**名字**；对应的值按同序放在 <see cref="Amf3Object.SealedValues"/>。</summary>
    public List<Amf3String> SealedMemberNames { get; } = new();

    public override string ToString() =>
        $"'{ClassName.Value}' (sealed {SealedMemberNames.Count}, dynamic {Dynamic}, external {Externalizable})";
}

/// <summary>
/// AMF3 <c>Object</c>（<c>0x0A</c>）。
///
/// <para>对象的 traits 有两种写法：内联（类名 + 成员名随后）或引用 traits 表。
/// 读取器两种都接受，并**始终**把解析结果放进 <see cref="Traits"/> 方便查看，
/// 同时用 <see cref="TraitsReferenceIndex"/> 记住原文是不是引用形态 —— 写出时按原文形态写。</para>
///
/// <para>密封成员的值是**按位顺序**的（名字在 traits 里），动态成员是名值对，
/// 所以两者分开存，不合并成一个字典。</para>
/// </summary>
public sealed class Amf3Object : Amf3ReferenceableValue
{
    public Amf3Object(Amf3Traits traits) => Traits = traits;

    public Amf3Traits Traits { get; set; }

    /// <summary>traits 表下标；<see langword="null"/> 表示 traits 内联。</summary>
    public int? TraitsReferenceIndex { get; set; }

    /// <summary>密封成员的值，顺序与 <see cref="Amf3Traits.SealedMemberNames"/> 一一对应。</summary>
    public List<Amf3Value> SealedValues { get; } = new();

    /// <summary>动态成员。名字用 <see cref="Amf3String"/> 而不是 <see cref="string"/>，
    /// 因为成员名同样可能是字符串引用。</summary>
    public List<KeyValuePair<Amf3String, Amf3Value>> DynamicMembers { get; } = new();

    public override Amf3Type Type => Amf3Type.Object;

    /// <summary>按名字取动态成员；没有该名时返回 <see langword="null"/>。有重名时返回第一个。</summary>
    public Amf3Value? TryGetDynamicMember(string name)
    {
        foreach (var (key, value) in DynamicMembers)
        {
            if (string.Equals(key.Value, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>密封成员（按 traits 的名字）与动态成员，按线上出现顺序枚举。</summary>
    public IEnumerable<KeyValuePair<string, Amf3Value>> EnumerateMembers()
    {
        var names = Traits.SealedMemberNames;
        for (var i = 0; i < SealedValues.Count; i++)
        {
            yield return new KeyValuePair<string, Amf3Value>(
                i < names.Count ? names[i].Value : string.Empty,
                SealedValues[i]);
        }

        foreach (var (key, value) in DynamicMembers)
        {
            yield return new KeyValuePair<string, Amf3Value>(key.Value, value);
        }
    }

    public override string ToString() =>
        ReferenceIndex is { } i ? $"@obj({i}) {Traits}" : Traits.ToString();
}

/// <summary>AMF3 对象引用（U29 的引用形态）：指向对象引用表里此前出现过的一个值。
/// 它的线上标记是 <c>0x0A</c>，所以 <see cref="Type"/> 报 <see cref="Amf3Type.Object"/>。</summary>
public sealed class Amf3ObjectReference : Amf3Value
{
    public Amf3ObjectReference(int index) => Index = index;

    public int Index { get; set; }

    public override Amf3Type Type => Amf3Type.Object;

    public override string ToString() => $"@ref({Index})";
}

/// <summary>AMF3 Vector 的公共部分：定长标志与引用下标。</summary>
public abstract class Amf3VectorBase : Amf3ReferenceableValue
{
    public bool FixedLength { get; set; }
}

/// <summary>AMF3 <c>Vector.&lt;int&gt;</c>（<c>0x0D</c>）：元素是 4 字节大端原始值。</summary>
public sealed class Amf3VectorInt : Amf3VectorBase
{
    public List<int> Items { get; } = new();

    public override Amf3Type Type => Amf3Type.VectorInt;

    public override string ToString() => $"Vector<int>({Items.Count})";
}

/// <summary>AMF3 <c>Vector.&lt;uint&gt;</c>（<c>0x0E</c>）：元素是 4 字节大端原始值。</summary>
public sealed class Amf3VectorUint : Amf3VectorBase
{
    public List<uint> Items { get; } = new();

    public override Amf3Type Type => Amf3Type.VectorUint;

    public override string ToString() => $"Vector<uint>({Items.Count})";
}

/// <summary>AMF3 <c>Vector.&lt;Number&gt;</c>（<c>0x0F</c>）：元素是 8 字节大端原始值。</summary>
public sealed class Amf3VectorDouble : Amf3VectorBase
{
    public List<double> Items { get; } = new();

    public override Amf3Type Type => Amf3Type.VectorDouble;

    public override string ToString() => $"Vector<Number>({Items.Count})";
}

/// <summary>AMF3 <c>Vector.&lt;Object&gt;</c>（<c>0x10</c>）：字段顺序为
/// <c>个数 → 定长标志 → 元素类型名 → 各元素</c>；元素是 AMF3 值。</summary>
public sealed class Amf3VectorObject : Amf3VectorBase
{
    public Amf3String ElementTypeName { get; set; } = new(string.Empty);

    public List<Amf3Value> Items { get; } = new();

    public override Amf3Type Type => Amf3Type.VectorObject;

    public override string ToString() => $"Vector<{ElementTypeName.Value}>({Items.Count})";
}

/// <summary>AMF3 <c>Dictionary</c>（<c>0x11</c>）：字段顺序为 <c>条目数 → 弱键标志 → 键值对</c>。
/// 键与值都是 AMF3 值（键不只可以是字符串）。</summary>
public sealed class Amf3Dictionary : Amf3ReferenceableValue
{
    public bool WeakKeys { get; set; }

    public List<KeyValuePair<Amf3Value, Amf3Value>> Entries { get; } = new();

    public override Amf3Type Type => Amf3Type.Dictionary;

    public override string ToString() => $"Dictionary({Entries.Count})";
}
