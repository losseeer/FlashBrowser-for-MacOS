using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF3 写出器 —— <see cref="Amf3Reader"/> 的逆操作。
///
/// <para><b>与 <see cref="Amf0Writer"/> 不同，这里必须是有状态的实例</b>：AMF3 有三张引用表，
/// 每写一个内联字符串 / 对象 / traits 就要往对应的表里加一项，之后写引用时下标才对得上。
/// 所以写一个 LSO 正文时要全程复用同一个实例（<see cref="Models.SolFile"/> 就是这么做的）。</para>
///
/// <para><b>写出时不做去重</b>：值模型里记着原文的引用形态（各值的 <c>ReferenceIndex</c>），
/// 是引用就写引用、是内联就写内联。这样「原样读进来再写出去」一定字节级相同，
/// 与 Flash 当初怎么去重无关 —— 我们只是把原文件的选择照抄回去。</para>
///
/// <para>引用下标只在**写出时校验**：超出当前表长说明值模型被人为改坏（比如手工构造了一个
/// 指向不存在项的下标），此时抛 <see cref="InvalidOperationException"/>，而不是写出一份坏文件。</para>
/// </summary>
public sealed class Amf3Writer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IBufferWriter<byte> _output;
    private int _stringCount;
    private int _objectCount;
    private int _traitCount;

    public Amf3Writer(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>便捷入口：写出单个值。多个值需要共享引用表时请自行持有实例。</summary>
    public static byte[] Write(Amf3Value value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        new Amf3Writer(buffer).WriteValue(value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>写一个作为名字使用的 U29S（容器属性名 / 类名 / 密封成员名），总是内联。
    /// 空名只作为结束标记出现，不入字符串表 —— 与读取器同规则。</summary>
    public void WriteName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        WriteUtf8Value(name);
    }

    public void WriteValue(Amf3Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteByte((byte)value.Type);

        switch (value)
        {
            case Amf3Undefined:
            case Amf3Null:
            case Amf3Boolean:
                // 无 payload：布尔的真假由标记本身表示。
                break;

            case Amf3Integer integer:
                if (integer.Value < Amf3Integer.MinValue || integer.Value > Amf3Integer.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"AMF3 integer {integer.Value} does not fit the 29-bit range " +
                        $"{Amf3Integer.MinValue}..{Amf3Integer.MaxValue}.");
                }

                WriteU29((uint)integer.Value & 0x1FFFFFFF);
                break;

            case Amf3Double number:
                WriteDouble(number.Value);
                break;

            case Amf3String text:
                WriteUtf8Value(text.Value, text.ReferenceIndex);
                break;

            case Amf3XmlDocument xmlDocument:
                WriteXml(xmlDocument.Value, xmlDocument.ReferenceIndex);
                break;

            case Amf3Xml xml:
                WriteXml(xml.Value, xml.ReferenceIndex);
                break;

            case Amf3Date date:
                if (BeginReferenceable(date.ReferenceIndex, "date"))
                {
                    // U29D-value：内联的日期也要有这一个 U29，只是一个「不是引用」的标志位，
                    // 其余位无意义，所以规范允许写 0x01。
                    WriteU29(1);
                    WriteDouble(date.Milliseconds);
                }

                break;

            case Amf3ByteArray byteArray:
                if (BeginReferenceable(byteArray.ReferenceIndex, "ByteArray"))
                {
                    WriteU29(((uint)byteArray.Bytes.Length << 1) | 1);
                    _output.Write(byteArray.Bytes);
                }

                break;

            case Amf3Array array:
                WriteArray(array);
                break;

            case Amf3Object obj:
                WriteObject(obj);
                break;

            case Amf3ObjectReference reference:
                ValidateIndex(reference.Index, _objectCount, "object");
                WriteU29((uint)reference.Index << 1);
                break;

            case Amf3VectorInt vectorInt:
                if (BeginReferenceable(vectorInt.ReferenceIndex, "Vector.<int>"))
                {
                    WriteVectorPrologue(vectorInt.Items.Count, vectorInt.FixedLength);
                    foreach (var item in vectorInt.Items)
                    {
                        WriteInt32(item);
                    }
                }

                break;

            case Amf3VectorUint vectorUint:
                if (BeginReferenceable(vectorUint.ReferenceIndex, "Vector.<uint>"))
                {
                    WriteVectorPrologue(vectorUint.Items.Count, vectorUint.FixedLength);
                    foreach (var item in vectorUint.Items)
                    {
                        WriteUInt32(item);
                    }
                }

                break;

            case Amf3VectorDouble vectorDouble:
                if (BeginReferenceable(vectorDouble.ReferenceIndex, "Vector.<Number>"))
                {
                    WriteVectorPrologue(vectorDouble.Items.Count, vectorDouble.FixedLength);
                    foreach (var item in vectorDouble.Items)
                    {
                        WriteDouble(item);
                    }
                }

                break;

            case Amf3VectorObject vectorObject:
                if (BeginReferenceable(vectorObject.ReferenceIndex, "Vector.<Object>"))
                {
                    WriteVectorPrologue(vectorObject.Items.Count, vectorObject.FixedLength);
                    WriteUtf8Value(vectorObject.ElementTypeName.Value, vectorObject.ElementTypeName.ReferenceIndex);
                    foreach (var item in vectorObject.Items)
                    {
                        WriteValue(item);
                    }
                }

                break;

            case Amf3Dictionary dictionary:
                if (BeginReferenceable(dictionary.ReferenceIndex, "Dictionary"))
                {
                    WriteU29(((uint)dictionary.Entries.Count << 1) | 1);
                    WriteByte(dictionary.WeakKeys ? (byte)1 : (byte)0);
                    foreach (var (key, item) in dictionary.Entries)
                    {
                        WriteValue(key);
                        WriteValue(item);
                    }
                }

                break;

            default:
                throw new NotSupportedException(
                    $"AMF3 type {value.Type} (0x{(byte)value.Type:x2}) cannot be written.");
        }
    }

    private void WriteArray(Amf3Array array)
    {
        if (!BeginReferenceable(array.ReferenceIndex, "array"))
        {
            return;
        }

        WriteU29(((uint)array.DenseItems.Count << 1) | 1);
        WriteU29(((uint)array.AssociativeMembers.Count << 1) | 1);

        foreach (var item in array.DenseItems)
        {
            WriteValue(item);
        }

        foreach (var (key, item) in array.AssociativeMembers)
        {
            WriteUtf8Value(key.Value, key.ReferenceIndex);
            WriteValue(item);
        }
    }

    private void WriteObject(Amf3Object obj)
    {
        if (obj.Traits.Externalizable)
        {
            throw new NotSupportedException(
                $"AMF3 externalizable class '{obj.Traits.ClassName.Value}' cannot be written: its payload has " +
                "no length prefix and its layout is defined by the class, so we cannot reproduce it.");
        }

        if (obj.ReferenceIndex is { } objectIndex)
        {
            ValidateIndex(objectIndex, _objectCount, "object");
            WriteU29((uint)objectIndex << 1);
            return;
        }

        _objectCount++;

        if (obj.TraitsReferenceIndex is { } traitIndex)
        {
            ValidateIndex(traitIndex, _traitCount, "trait");

            // bit0 = 1 对象内联，bit1 = 0 traits 为引用，bit2 起是 traits 下标。
            WriteU29(1u | ((uint)traitIndex << 2));
        }
        else
        {
            var traits = obj.Traits;
            var sealedCount = (uint)traits.SealedMemberNames.Count;

            // bit0 = 1 对象内联，bit1 = 1 traits 内联，bit2 = externalizable（见上，恒 0），
            // bit3 = dynamic，bit4 起为密封成员个数。
            WriteU29(1u | (1u << 1) | (traits.Dynamic ? 1u << 3 : 0u) | (sealedCount << 4));
            WriteUtf8Value(traits.ClassName.Value);

            foreach (var name in traits.SealedMemberNames)
            {
                WriteUtf8Value(name.Value);
            }

            _traitCount++;
        }

        if (obj.SealedValues.Count != obj.Traits.SealedMemberNames.Count)
        {
            throw new InvalidOperationException(
                $"AMF3 object of class '{obj.Traits.ClassName.Value}' has {obj.SealedValues.Count} sealed " +
                $"value(s) but its traits declare {obj.Traits.SealedMemberNames.Count} sealed member name(s).");
        }

        foreach (var sealedValue in obj.SealedValues)
        {
            WriteValue(sealedValue);
        }

        if (obj.Traits.Dynamic)
        {
            foreach (var (key, item) in obj.DynamicMembers)
            {
                WriteUtf8Value(key.Value, key.ReferenceIndex);
                WriteValue(item);
            }

            // 空名 = 动态成员结束标记。
            WriteByte(0x01);
        }
    }

    private void WriteVectorPrologue(int count, bool fixedLength)
    {
        WriteU29(((uint)count << 1) | 1);
        WriteByte(fixedLength ? (byte)1 : (byte)0);
    }

    /// <summary>写 XML / XMLDocument：文本形态与字符串同形，但引用走对象表、内容不进字符串表。</summary>
    private void WriteXml(string text, int? referenceIndex)
    {
        if (!BeginReferenceable(referenceIndex, "XML"))
        {
            return;
        }

        var bytes = Utf8.GetBytes(text);
        WriteU29(((uint)bytes.Length << 1) | 1);
        _output.Write(bytes);
    }

    /// <summary>
    /// 写「引用或内联」前缀。返回 <see langword="true"/> 表示这是内联值、调用方还要写内容；
    /// 返回 <see langword="false"/> 表示引用已经写完、调用方什么都不用做。
    /// </summary>
    private bool BeginReferenceable(int? referenceIndex, string kind)
    {
        if (referenceIndex is { } index)
        {
            ValidateIndex(index, _objectCount, kind);
            WriteU29((uint)index << 1);
            return false;
        }

        // 与读取器同序：先登记对象表，再读/写内容 —— 这样自引用（循环结构）下标也对得上。
        _objectCount++;
        return true;
    }

    /// <summary>写 U29S 字符串：有引用下标则写引用，否则写内联字面量并按规则入表。</summary>
    private void WriteUtf8Value(string value, int? referenceIndex = null)
    {
        if (referenceIndex is { } index)
        {
            ValidateIndex(index, _stringCount, "string");
            WriteU29((uint)index << 1);
            return;
        }

        var bytes = Utf8.GetBytes(value);
        WriteU29(((uint)bytes.Length << 1) | 1);
        _output.Write(bytes);

        // 空串不入表：它是结束标记，规范也说它从不按引用发送。与读取器 AddString 同规则。
        if (bytes.Length > 0)
        {
            _stringCount++;
        }
    }

    private static void ValidateIndex(int index, int tableCount, string what)
    {
        if (index < 0 || index >= tableCount)
        {
            throw new InvalidOperationException(
                $"AMF3 {what} reference index {index} is out of range: the writer's {what} table holds " +
                $"{tableCount} entries at this point.");
        }
    }

    /// <summary>
    /// 写 U29 变长无符号整数（最多 29 位）。前三个字节各 7 位、最高位作延续标志，
    /// 第四个字节贡献完整 8 位。**这里总是写最紧凑的形式**：若输入是别人写的非紧凑 U29，
    /// 读进来的数值不变、写出去会变成紧凑形式，字节就不再相同。
    /// </summary>
    public void WriteU29(uint value)
    {
        if (value > 0x1FFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "U29 holds at most 29 bits");
        }

        if (value < 0x80)
        {
            WriteByte((byte)value);
            return;
        }

        if (value < 0x4000)
        {
            WriteByte((byte)(0x80 | (value >> 7)));
            WriteByte((byte)(value & 0x7F));
            return;
        }

        if (value < 0x200000)
        {
            WriteByte((byte)(0x80 | (value >> 14)));
            WriteByte((byte)(0x80 | ((value >> 7) & 0x7F)));
            WriteByte((byte)(value & 0x7F));
            return;
        }

        WriteByte((byte)(0x80 | (value >> 22)));
        WriteByte((byte)(0x80 | ((value >> 15) & 0x7F)));
        WriteByte((byte)(0x80 | ((value >> 8) & 0x7F)));
        WriteByte((byte)(value & 0xFF));
    }

    public void WriteByte(byte value)
    {
        var span = _output.GetSpan(1);
        span[0] = value;
        _output.Advance(1);
    }

    public void WriteInt32(int value)
    {
        var span = _output.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        _output.Advance(4);
    }

    public void WriteUInt32(uint value)
    {
        var span = _output.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        _output.Advance(4);
    }

    public void WriteDouble(double value)
    {
        var span = _output.GetSpan(8);
        BinaryPrimitives.WriteDoubleBigEndian(span, value);
        _output.Advance(8);
    }
}
