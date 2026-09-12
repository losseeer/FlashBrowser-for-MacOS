using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF0 写出器 —— <see cref="Amf0Reader"/> 的逆操作。
///
/// 全部多字节整数与浮点均为**大端**（AMF0 规范要求），所以不能直接用
/// <see cref="System.IO.BinaryWriter"/>（它是小端），改用 <see cref="BinaryPrimitives"/>。
/// </summary>
public static class Amf0Writer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>把一个值序列化成 AMF0 字节。</summary>
    public static byte[] Write(Amf0Value value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>把一个值追加写进 <paramref name="output"/>。</summary>
    public static void Write(IBufferWriter<byte> output, Amf0Value value)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);

        WriteByte(output, (byte)value.Type);

        switch (value)
        {
            case Amf0Number number:
                WriteDouble(output, number.Value);
                break;

            case Amf0Boolean boolean:
                WriteByte(output, boolean.Value ? (byte)1 : (byte)0);
                break;

            case Amf0String text:
                WriteShortString(output, text.Value);
                break;

            case Amf0Object obj:
                WriteMembers(output, obj.Members);
                break;

            case Amf0Null:
            case Amf0Undefined:
                // 无 payload。
                break;

            case Amf0Reference reference:
                WriteUInt16(output, reference.Index);
                break;

            case Amf0EcmaArray ecmaArray:
                WriteUInt32(output, ecmaArray.DeclaredCount);
                WriteMembers(output, ecmaArray.Members);
                break;

            case Amf0StrictArray strictArray:
                WriteUInt32(output, checked((uint)strictArray.Items.Count));
                foreach (var item in strictArray.Items)
                {
                    Write(output, item);
                }

                break;

            case Amf0Date date:
                WriteDouble(output, date.Milliseconds);
                WriteInt16(output, date.TimeZoneMinutes);
                break;

            case Amf0LongString longString:
                WriteLongString(output, longString.Value);
                break;

            case Amf0XmlDocument xml:
                WriteLongString(output, xml.Value);
                break;

            case Amf0TypedObject typed:
                WriteShortString(output, typed.ClassName);
                WriteMembers(output, typed.Members);
                break;

            default:
                throw new NotSupportedException(
                    $"AMF0 type {value.Type} (0x{(byte)value.Type:x2}) cannot be written.");
        }
    }

    /// <summary>写出成员表到 <c>00 00 09</c> 结束标记为止（不含外层标记）。</summary>
    public static void WriteMembers(IBufferWriter<byte> output, IReadOnlyList<KeyValuePair<string, Amf0Value>> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        foreach (var (name, value) in members)
        {
            WriteShortString(output, name);
            Write(output, value);
        }

        WriteObjectEnd(output);
    }

    /// <summary>写出 AMF0 对象结束标记 <c>00 00 09</c>。</summary>
    public static void WriteObjectEnd(IBufferWriter<byte> output)
    {
        WriteUInt16(output, 0);
        WriteByte(output, (byte)Amf0Type.ObjectEnd);
    }

    /// <summary>写 <c>u16</c> 长度 + UTF-8 字节。</summary>
    public static void WriteShortString(IBufferWriter<byte> output, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Utf8.GetBytes(value);
        WriteUInt16(output, checked((ushort)bytes.Length));
        output.Write(bytes);
    }

    /// <summary>写 <c>u32</c> 长度 + UTF-8 字节（长字符串 / XML 文档的形态）。</summary>
    public static void WriteLongString(IBufferWriter<byte> output, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Utf8.GetBytes(value);
        WriteUInt32(output, checked((uint)bytes.Length));
        output.Write(bytes);
    }

    public static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }

    public static void WriteUInt16(IBufferWriter<byte> output, ushort value)
    {
        var span = output.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        output.Advance(2);
    }

    public static void WriteInt16(IBufferWriter<byte> output, short value)
    {
        var span = output.GetSpan(2);
        BinaryPrimitives.WriteInt16BigEndian(span, value);
        output.Advance(2);
    }

    public static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        var span = output.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        output.Advance(4);
    }

    public static void WriteDouble(IBufferWriter<byte> output, double value)
    {
        var span = output.GetSpan(8);
        BinaryPrimitives.WriteDoubleBigEndian(span, value);
        output.Advance(8);
    }
}
