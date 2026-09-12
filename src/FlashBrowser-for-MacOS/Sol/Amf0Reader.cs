using System.Buffers.Binary;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF0 读取器。按字节流顺序读值，一个实例可连续读多个值（LSO 的成员表就是这样用的）。
///
/// 设计取舍：
/// <list type="bullet">
/// <item>遇到无法确定语义的标记（<c>0x04</c> MovieClip、<c>0x0D</c> Unsupported、<c>0x0E</c> Recordset、
/// <c>0x11</c> AvmPlus）时**显式抛错**，不做「跳过」或「当空值」这类静默降级 —— 静默降级会让
/// 调用方拿到一份看似解析成功、实际丢失内容的存档。</item>
/// <item>UTF-8 用严格模式解码：非法字节序列抛 <see cref="InvalidDataException"/> 并带上偏移量，
/// 而不是用 U+FFFD 替换。替换会破坏 round-trip（写字回时已不是原字节），且问题会被藏起来。</item>
/// </list>
/// </summary>
public sealed class Amf0Reader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _buffer;
    private int _position;

    public Amf0Reader(byte[] buffer, int position = 0)
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
    public Amf0Value ReadValue()
    {
        var offset = _position;
        var marker = ReadByte();
        return ReadValueOfType((Amf0Type)marker, offset);
    }

    /// <summary>读一个 AMF0 短字符串（<c>u16</c> 长度 + UTF-8）。
    /// LSO 的成员名用的就是这种编码 —— 与 AMF0 对象成员名同形。</summary>
    public string ReadShortString() => ReadUtf8(ReadUInt16());

    /// <summary>读一个 AMF0 长字符串形态的值体（<c>u32</c> 长度 + UTF-8）。</summary>
    public string ReadLongString() => ReadUtf8(checked((int)ReadUInt32()));

    /// <summary>读一个对象成员表（到 <c>00 00 09</c> 为止），不含起止标记本身。</summary>
    public List<KeyValuePair<string, Amf0Value>> ReadMembers()
    {
        var members = new List<KeyValuePair<string, Amf0Value>>();

        while (true)
        {
            var lengthOffset = _position;
            var length = ReadUInt16();

            if (length == 0)
            {
                // 规范：0 长度成员名 + 0x09 构成对象结束标记 00 00 09。
                var marker = ReadByte();
                if (marker != (byte)Amf0Type.ObjectEnd)
                {
                    throw new InvalidDataException(
                        $"AMF0 object member name at offset {lengthOffset} has zero length, but the following " +
                        $"byte is 0x{marker:x2} instead of the 0x09 end marker.");
                }

                return members;
            }

            var name = ReadUtf8(length);
            members.Add(new KeyValuePair<string, Amf0Value>(name, ReadValue()));
        }
    }

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position, 2));
        _position += 2;
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

    public short ReadInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadInt16BigEndian(_buffer.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    private Amf0Value ReadValueOfType(Amf0Type type, int markerOffset)
    {
        switch (type)
        {
            case Amf0Type.Number:
                return new Amf0Number(ReadDouble());
            case Amf0Type.Boolean:
                return new Amf0Boolean(ReadByte() != 0);
            case Amf0Type.String:
                return new Amf0String(ReadShortString());
            case Amf0Type.Object:
                {
                    var obj = new Amf0Object();
                    obj.Members.AddRange(ReadMembers());
                    return obj;
                }
            case Amf0Type.Null:
                return new Amf0Null();
            case Amf0Type.Undefined:
                return new Amf0Undefined();
            case Amf0Type.Reference:
                return new Amf0Reference(ReadUInt16());
            case Amf0Type.EcmaArray:
                {
                    var declaredCount = ReadUInt32();
                    var array = new Amf0EcmaArray(declaredCount);
                    array.Members.AddRange(ReadMembers());
                    return array;
                }

            case Amf0Type.StrictArray:
                {
                    var count = ReadUInt32();
                    var array = new Amf0StrictArray();
                    for (var i = 0; i < count; i++)
                    {
                        array.Items.Add(ReadValue());
                    }

                    return array;
                }

            case Amf0Type.Date:
                {
                    var milliseconds = ReadDouble();
                    var timeZone = ReadInt16();
                    return new Amf0Date(milliseconds, timeZone);
                }

            case Amf0Type.LongString:
                return new Amf0LongString(ReadLongString());
            case Amf0Type.XmlDocument:
                return new Amf0XmlDocument(ReadLongString());
            case Amf0Type.TypedObject:
                {
                    var className = ReadShortString();
                    var typed = new Amf0TypedObject(className);
                    typed.Members.AddRange(ReadMembers());
                    return typed;
                }

            case Amf0Type.ObjectEnd:
                throw new InvalidDataException(
                    $"AMF0 marker 0x09 (ObjectEnd) at offset {markerOffset} is a terminator, not a value.");

            case Amf0Type.MovieClip:
            case Amf0Type.Unsupported:
            case Amf0Type.Recordset:
            case Amf0Type.AvmPlus:
            default:
                throw new NotSupportedException(
                    $"AMF0 marker 0x{(byte)type:x2} ({type}) at offset {markerOffset} has no defined AMF0 payload; " +
                    "cannot read it without guessing.");
        }
    }

    private string ReadUtf8(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new InvalidDataException($"AMF0 string length at offset {_position} is negative ({byteCount}).");
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
                $"AMF0 string at offset {offset} ({byteCount} bytes) is not valid UTF-8. " +
                "Decoding it with replacement characters would silently break byte-level round-trip.",
                exception);
        }
    }

    private void EnsureAvailable(int byteCount)
    {
        if (byteCount < 0 || _position + byteCount > _buffer.Length)
        {
            throw new InvalidDataException(
                $"AMF0 stream ended early: need {byteCount} byte(s) at offset {_position}, " +
                $"but only {Remaining} remain (buffer length {_buffer.Length}).");
        }
    }
}
