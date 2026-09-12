using System.Buffers.Binary;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF3 读取器。按字节流顺序读值，一个实例可连续读多个值（LSO 的成员表就是这样用的）。
///
/// <para><b>U29</b>：AMF3 的变长无符号整数，1~4 字节、最多 29 位。前三个字节各贡献 7 位（最高位
/// 是「还有后续字节」标志），第四个字节贡献完整的 8 位。</para>
///
/// <para><b>对象与 traits 共用一个 U29</b>。这是最容易读错的地方：对象标记 <c>0x0A</c> 之后
/// **只有一个** U29，位序如下（<c>Adobe AMF 3 Specification</c> 里那段「第一位是 1、第二位是 1、
/// 第三位是 0、第四位是 dynamic」的描述就是在逐位说这个布局，所以读起来很绕）：</para>
/// <code>
///   bit0      1 = 对象内联；0 = 对象引用（下标 = u29 &gt;&gt; 1）
///   bit1      1 = traits 内联；0 = traits 引用（下标 = u29 &gt;&gt; 2）
///   bit2      externalizable
///   bit3      dynamic
///   bit4 起   密封成员个数
/// </code>
/// <para>实测反证：<c>pvz.sol</c> 的 <c>saveData</c> 对象那一字节是 <c>0x0b</c> = <c>0b1011</c>，
/// 即「对象内联 + traits 内联 + 非 externalizable + dynamic + 0 个密封成员」，
/// 与后面跟着的「空类名 + 5 组动态名值对 + 空名结束」完全吻合。</para>
///
/// <para><b>显式失败优先</b>：与 <see cref="Amf0Reader"/> 一致，不做静默降级 ——
/// 引用下标越界、externalizable 对象、名字位置出现字符串引用、非法 UTF-8、嵌套过深，
/// 一律抛异常并带上偏移量，而不是给出一份看似解析成功的错数据。</para>
/// </summary>
public sealed class Amf3Reader
{
    /// <summary>嵌套深度上限。防御恶意 / 损坏文件把递归撑爆栈。</summary>
    public const int MaxDepth = 512;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _buffer;

    /// <summary>字符串引用表。每次读到**内联** U29S 就入表 —— 与写出一侧保持同一规则。</summary>
    private readonly List<string> _strings = new();

    /// <summary>对象引用表。只记偏移，用于报错时定位与判断越界。</summary>
    private readonly List<int> _objectOffsets = new();

    /// <summary>traits 引用表。</summary>
    private readonly List<Amf3Traits> _traits = new();

    private int _position;

    public Amf3Reader(byte[] buffer, int position = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (position < 0 || position > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "position is outside the buffer");
        }

        _buffer = buffer;
        _position = position;
    }

    /// <summary>下一个待读字节的绝对偏移。异常消息里用它定位问题。</summary>
    public int Position => _position;

    public int Remaining => _buffer.Length - _position;

    public bool AtEnd => _position >= _buffer.Length;

    /// <summary>读类型标记并读出对应的值。</summary>
    public Amf3Value ReadValue() => ReadValue(depth: 1);

    /// <summary>
    /// 读一个只作为**名字**使用的 U29S 字符串（LSO 的容器层属性名用的就是这种编码）。
    ///
    /// 名字在模型里是普通 <see cref="string"/>，没有地方保存引用下标，所以遇到引用形态会显式报错 ——
    /// 悄悄展开成字面量会让写回的字节与原文件不同。
    /// </summary>
    public string ReadName(string what)
    {
        var offset = _position;
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            throw new NotSupportedException(
                $"{what} at offset {offset} is written as a string reference (table index {raw >> 1}). " +
                "Names are kept as plain strings, so expanding the reference would silently change the bytes " +
                "written back. Referenced names are not supported yet.");
        }

        var value = ReadUtf8(ByteLength(raw >> 1, offset, what));
        AddString(value);
        return value;
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    /// <summary>读 U29 变长无符号整数（最多 29 位，见类型注释）。</summary>
    public uint ReadU29()
    {
        EnsureAvailable(1);
        var current = _buffer[_position++];
        if ((current & 0x80) == 0)
        {
            return current;
        }

        uint value = (uint)(current & 0x7F);

        EnsureAvailable(1);
        current = _buffer[_position++];
        if ((current & 0x80) == 0)
        {
            return (value << 7) | current;
        }

        value = (value << 7) | (uint)(current & 0x7F);

        EnsureAvailable(1);
        current = _buffer[_position++];
        if ((current & 0x80) == 0)
        {
            return (value << 7) | current;
        }

        value = (value << 7) | (uint)(current & 0x7F);

        // 第四个字节贡献完整 8 位，它的最高位不再是延续标志。
        EnsureAvailable(1);
        current = _buffer[_position++];
        return (value << 8) | current;
    }

    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public double ReadDouble()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadDoubleBigEndian(_buffer.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    private Amf3Value ReadValue(int depth)
    {
        var offset = _position;
        var marker = ReadByte();

        if (!Enum.IsDefined(typeof(Amf3Type), marker))
        {
            throw new InvalidDataException(
                $"AMF3 marker 0x{marker:x2} at offset {offset} is not a defined AMF3 type.");
        }

        return ReadValueOfType((Amf3Type)marker, offset, depth);
    }

    private Amf3Value ReadValueOfType(Amf3Type type, int offset, int depth)
    {
        switch (type)
        {
            case Amf3Type.Undefined:
                return new Amf3Undefined();
            case Amf3Type.Null:
                return new Amf3Null();
            case Amf3Type.False:
                return new Amf3Boolean(false);
            case Amf3Type.True:
                return new Amf3Boolean(true);

            case Amf3Type.Integer:
                // U29 的 29 位按补码解释：左移 3 位再算术右移回去，就是把第 28 位当符号位。
                return new Amf3Integer((int)(ReadU29() << 3) >> 3);

            case Amf3Type.Double:
                return new Amf3Double(ReadDouble());

            case Amf3Type.String:
                return ReadStringValue();

            case Amf3Type.XmlDocument:
                return ReadXml(offset, document: true);

            case Amf3Type.Xml:
                return ReadXml(offset, document: false);

            case Amf3Type.Date:
                return ReadDate(offset);

            case Amf3Type.ByteArray:
                return ReadByteArray(offset);

            case Amf3Type.Array:
                return ReadArray(offset, depth);

            case Amf3Type.Object:
                return ReadObject(offset, depth);

            case Amf3Type.VectorInt:
                return ReadVectorInt(offset);

            case Amf3Type.VectorUint:
                return ReadVectorUint(offset);

            case Amf3Type.VectorDouble:
                return ReadVectorDouble(offset);

            case Amf3Type.VectorObject:
                return ReadVectorObject(offset, depth);

            case Amf3Type.Dictionary:
                return ReadDictionary(offset, depth);

            default:
                throw new NotSupportedException(
                    $"AMF3 marker 0x{(byte)type:x2} ({type}) at offset {offset} is not implemented.");
        }
    }

    private Amf3String ReadStringValue()
    {
        var offset = _position;
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            var index = StringIndex(raw >> 1, offset);
            return new Amf3String(_strings[index]) { ReferenceIndex = index };
        }

        var value = ReadUtf8(ByteLength(raw >> 1, offset, "AMF3 string"));
        AddString(value);
        return new Amf3String(value);
    }

    /// <summary>
    /// XML / XMLDocument：文本形态仍是 U29（<c>U29X</c>），但引用用的是**对象**引用表而不是字符串表，
    /// 文本内容本身也不进字符串表（规范里它是 <c>U29X-value</c>，不是 <c>UTF-8-vr</c>）。
    /// 这一点如果按字符串处理，后面所有引用下标都会错位。
    /// </summary>
    private Amf3Value ReadXml(int offset, bool document)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            var index = ObjectIndex(raw >> 1, offset);
            return document
                ? new Amf3XmlDocument(string.Empty) { ReferenceIndex = index }
                : new Amf3Xml(string.Empty) { ReferenceIndex = index };
        }

        var what = document ? "AMF3 XMLDocument" : "AMF3 XML";
        var text = ReadUtf8(ByteLength(raw >> 1, offset, what));
        RegisterObject(offset);

        return document ? new Amf3XmlDocument(text) : new Amf3Xml(text);
    }

    /// <summary>
    /// 内联字符串入表。规则：**只收非空串**。
    /// 空串是动态成员 / traits 的结束标记，规范也说它「从不按引用发送」，
    /// 所以把它排除在表外，索引才和 Flash 自己算的一致 —— 顺便让「空名 = 结束」天然不会污染表。
    /// </summary>
    private void AddString(string value)
    {
        if (value.Length > 0)
        {
            _strings.Add(value);
        }
    }

    private Amf3Value ReadDate(int offset)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3Date(0) { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        RegisterObject(offset);
        return new Amf3Date(ReadDouble());
    }

    private Amf3Value ReadByteArray(int offset)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3ByteArray(Array.Empty<byte>()) { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        var length = ByteLength(raw >> 1, offset, "AMF3 ByteArray");
        RegisterObject(offset);

        EnsureAvailable(length);
        var bytes = _buffer.AsSpan(_position, length).ToArray();
        _position += length;
        return new Amf3ByteArray(bytes);
    }

    private Amf3Value ReadArray(int offset, int depth)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3ObjectReference(ObjectIndex(raw >> 1, offset));
        }

        EnterNested(depth, offset, "array");
        RegisterObject(offset);

        var denseCount = ElementCount(raw >> 1, offset, "AMF3 array dense part");
        var associativeCount = ElementCount(ReadU29() >> 1, offset, "AMF3 array associative part");

        var array = new Amf3Array();
        for (var i = 0; i < denseCount; i++)
        {
            array.DenseItems.Add(ReadValue(depth + 1));
        }

        for (var i = 0; i < associativeCount; i++)
        {
            var key = ReadStringValue();
            array.AssociativeMembers.Add(new KeyValuePair<Amf3String, Amf3Value>(key, ReadValue(depth + 1)));
        }

        return array;
    }

    private Amf3Value ReadObject(int offset, int depth)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3ObjectReference(ObjectIndex(raw >> 1, offset));
        }

        EnterNested(depth, offset, "object");
        RegisterObject(offset);

        Amf3Traits traits;
        int? traitsReferenceIndex = null;

        if (((raw >> 1) & 1) == 0)
        {
            var index = TraitIndex(raw >> 2, offset);
            traits = _traits[index];
            traitsReferenceIndex = index;
        }
        else
        {
            traits = ReadInlineTraits(raw, offset);
        }

        if (traits.Externalizable)
        {
            throw new NotSupportedException(
                $"AMF3 object of class '{traits.ClassName.Value}' at offset {offset} is externalizable. " +
                "Its payload has no length prefix and its layout is defined by the class itself " +
                "(AMF3 spec: a private agreement between client and server), so it cannot be read without " +
                "a per-class decoder. Guessing a boundary here would corrupt the archive.");
        }

        var obj = new Amf3Object(traits)
        {
            TraitsReferenceIndex = traitsReferenceIndex,
        };

        for (var i = 0; i < traits.SealedMemberNames.Count; i++)
        {
            obj.SealedValues.Add(ReadValue(depth + 1));
        }

        if (traits.Dynamic)
        {
            while (true)
            {
                var name = ReadStringValue();

                // 空名字是动态成员的结束标记。
                if (name.Value.Length == 0 && name.ReferenceIndex is null)
                {
                    break;
                }

                obj.DynamicMembers.Add(new KeyValuePair<Amf3String, Amf3Value>(name, ReadValue(depth + 1)));
            }
        }

        return obj;
    }

    private Amf3Traits ReadInlineTraits(uint raw, int offset)
    {
        var externalizable = ((raw >> 2) & 1) != 0;
        var dynamic = ((raw >> 3) & 1) != 0;
        var sealedCount = ElementCount(raw >> 4, offset, "AMF3 sealed member count");

        var className = ReadStringValue();

        var traits = new Amf3Traits(className, externalizable, dynamic);

        if (externalizable)
        {
            // 规范：externalizable 变体的密封成员个数恒为 0，其后是类自己定义的字节。读不下去，
            // 但类名已经拿到，把它带进异常消息便于定位（见 ReadObject 里抛错的位置）。
            return traits;
        }

        for (var i = 0; i < sealedCount; i++)
        {
            traits.SealedMemberNames.Add(ReadStringValue());
        }

        _traits.Add(traits);
        return traits;
    }

    private Amf3Value ReadVectorInt(int offset)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3VectorInt { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        var count = ElementCount(raw >> 1, offset, "AMF3 Vector.<int>");
        RegisterObject(offset);

        var vector = new Amf3VectorInt { FixedLength = ReadByte() != 0 };
        for (var i = 0; i < count; i++)
        {
            vector.Items.Add(ReadInt32());
        }

        return vector;
    }

    private Amf3Value ReadVectorUint(int offset)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3VectorUint { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        var count = ElementCount(raw >> 1, offset, "AMF3 Vector.<uint>");
        RegisterObject(offset);

        var vector = new Amf3VectorUint { FixedLength = ReadByte() != 0 };
        for (var i = 0; i < count; i++)
        {
            vector.Items.Add(ReadUInt32());
        }

        return vector;
    }

    private Amf3Value ReadVectorDouble(int offset)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3VectorDouble { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        var count = ElementCount(raw >> 1, offset, "AMF3 Vector.<Number>");
        RegisterObject(offset);

        var vector = new Amf3VectorDouble { FixedLength = ReadByte() != 0 };
        for (var i = 0; i < count; i++)
        {
            vector.Items.Add(ReadDouble());
        }

        return vector;
    }

    private Amf3Value ReadVectorObject(int offset, int depth)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3VectorObject { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        EnterNested(depth, offset, "Vector.<Object>");
        var count = ElementCount(raw >> 1, offset, "AMF3 Vector.<Object>");
        RegisterObject(offset);

        var vector = new Amf3VectorObject
        {
            // 字段顺序：定长标志在前，元素类型名在后。
            FixedLength = ReadByte() != 0,
            ElementTypeName = ReadStringValue(),
        };

        for (var i = 0; i < count; i++)
        {
            vector.Items.Add(ReadValue(depth + 1));
        }

        return vector;
    }

    private Amf3Value ReadDictionary(int offset, int depth)
    {
        var raw = ReadU29();

        if ((raw & 1) == 0)
        {
            return new Amf3Dictionary { ReferenceIndex = ObjectIndex(raw >> 1, offset) };
        }

        EnterNested(depth, offset, "Dictionary");
        var count = ElementCount(raw >> 1, offset, "AMF3 Dictionary");
        RegisterObject(offset);

        var dictionary = new Amf3Dictionary { WeakKeys = ReadByte() != 0 };
        for (var i = 0; i < count; i++)
        {
            var key = ReadValue(depth + 1);
            dictionary.Entries.Add(new KeyValuePair<Amf3Value, Amf3Value>(key, ReadValue(depth + 1)));
        }

        return dictionary;
    }

    private void RegisterObject(int offset)
    {
        _objectOffsets.Add(offset);
    }

    private int StringIndex(uint raw, int offset)
    {
        var index = checked((int)raw);
        if (index >= _strings.Count)
        {
            throw new InvalidDataException(
                $"AMF3 string reference {index} at offset {offset} is out of range: " +
                $"the string table holds {_strings.Count} entries.");
        }

        return index;
    }

    private int ObjectIndex(uint raw, int offset)
    {
        var index = checked((int)raw);
        if (index >= _objectOffsets.Count)
        {
            throw new InvalidDataException(
                $"AMF3 object reference {index} at offset {offset} is out of range: " +
                $"the object table holds {_objectOffsets.Count} entries.");
        }

        return index;
    }

    private int TraitIndex(uint raw, int offset)
    {
        var index = checked((int)raw);
        if (index >= _traits.Count)
        {
            throw new InvalidDataException(
                $"AMF3 trait reference {index} at offset {offset} is out of range: " +
                $"the trait table holds {_traits.Count} entries.");
        }

        return index;
    }

    /// <summary>校验字节长度（U29S 字符串、ByteArray）。</summary>
    private int ByteLength(uint raw, int offset, string what)
    {
        var length = checked((int)raw);
        if (length > Remaining)
        {
            throw new InvalidDataException(
                $"{what} at offset {offset} declares {length} bytes, but only {Remaining} remain " +
                $"(buffer length {_buffer.Length}).");
        }

        return length;
    }

    /// <summary>
    /// 校验元素个数。每个 AMF3 元素至少占 1 字节，所以「个数 &gt; 剩余字节数」一定是坏数据 ——
    /// 这道检查同时挡住了「声明的个数大到把内存吃光」这类输入。
    /// </summary>
    private int ElementCount(uint raw, int offset, string what)
    {
        var count = checked((int)raw);
        if (count > Remaining)
        {
            throw new InvalidDataException(
                $"{what} at offset {offset} declares {count} elements, but only {Remaining} bytes remain " +
                $"(buffer length {_buffer.Length}). Every AMF3 element takes at least one byte.");
        }

        return count;
    }

    private static void EnterNested(int depth, int offset, string what)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException(
                $"AMF3 nesting is deeper than {MaxDepth} levels at offset {offset} ({what}). " +
                "Refusing to recurse further.");
        }
    }

    private string ReadUtf8(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new InvalidDataException($"AMF3 string length at offset {_position} is negative ({byteCount}).");
        }

        EnsureAvailable(byteCount);
        var offset = _position;

        try
        {
            var text = StrictUtf8.GetString(_buffer, offset, byteCount);
            _position += byteCount;
            return text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"AMF3 string at offset {offset} ({byteCount} bytes) is not valid UTF-8. " +
                "Decoding it with replacement characters would silently break byte-level round-trip.",
                exception);
        }
    }

    private void EnsureAvailable(int byteCount)
    {
        if (byteCount < 0 || _position + byteCount > _buffer.Length)
        {
            throw new InvalidDataException(
                $"AMF3 stream ended early: need {byteCount} byte(s) at offset {_position}, " +
                $"but only {Remaining} remain (buffer length {_buffer.Length}).");
        }
    }
}
