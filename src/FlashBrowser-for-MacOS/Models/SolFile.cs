using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using FlashBrowserForMacOS.Sol;

namespace FlashBrowserForMacOS.Models;

/// <summary>LSO（Local Shared Object，即 Flash 的 <c>.sol</c>）正文的 AMF 版本。</summary>
public enum SolFormat : byte
{
    /// <summary>Action Message Format 0（Flash Player 6+）。</summary>
    Amf0 = 0x00,

    /// <summary>Action Message Format 3（Flash Player 9+ / ActionScript 3）。</summary>
    Amf3 = 0x03,
}

/// <summary>LSO 文件头。头之后紧跟正文（<see cref="BodyOffset"/>）。</summary>
public sealed record SolHeader(string Name, SolFormat Format, uint DeclaredLength, int BodyOffset);

/// <summary>
/// 一个 LSO（<c>.sol</c>）文件：文件头 + 属性表。
///
/// <para><b>文件头</b>（本机 4 个 fixture 逐个字节核对过）：</para>
/// <code>
///   00 BF                              magic
///   u32                                payload length（= 文件总长 − 6）
///   "TCSO" 00 04 00 00 00 00           固定块
///   u16 + bytes                        name（UTF-8，实测为 ASCII）
///   00 00 00                           固定 3 字节 padding
///   1 byte                             AMF 版本（0x00=AMF0 / 0x03=AMF3）
/// </code>
///
/// <para><b>正文（AMF0）</b>：不是标准 AMF0 对象，而是 Flash 自己的方言 —— 成员表被剥掉了
/// 起止标记，并给每个属性补了一个 <c>0x00</c> 字节：</para>
/// <code>
///   repeat { u16 keyLength | key | amf0 value | 0x00 }
/// </code>
/// <para>实测依据（<c>settings.sol</c>，845 B）：属性 <c>allowThirdPartyLSOAccess</c> 的值为
/// <c>Boolean true</c>，占 <c>01 01</c> 两字节，其后还有一个 <c>0x00</c>；下一个属性的名字长度字段
/// 出现在偏移 29 而不是 28 —— 这个 <c>0x00</c> 必须归属于前一个值。文件末尾同样以
/// <c>00 00 00 00 00 00 00 00 00</c>（Number 0.0）<b>再加一个</b> <c>0x00</c> 结束，与「最后一个属性也带分隔字节」一致。
/// 该规则在另外 3 个 AMF3 fixture 上同样成立（<c>mao.sol</c> 正文为 <c>0B "level" 04 06 00</c>，
/// 其中 <c>0B</c> 是 U29S 的长度前缀、<c>04 06</c> 是整数 6），因此它是 LSO 容器层的规则，与 AMF 版本无关。
/// 换版本只换了「名字与值的编码」，分隔规则一模一样：</para>
/// <code>
///   repeat { u29s keyLength+key | amf3 value | 0x00 }
/// </code>
/// <para>旁证：Ruffle 使用的 <c>flash-lso</c> 解析器把两种版本都写成
/// <c>separated_list0(0x00) + 尾部 0x00</c>，与上面的实测一致。</para>
/// <para>注意：<b>嵌套</b>在值里的对象走的是标准 AMF（AMF0 成员表 + <c>00 00 09</c> 结束标记；
/// AMF3 见 <see cref="Amf3Reader"/>），没有这个分隔字节。</para>
/// </summary>
public sealed class SolFile
{
    /// <summary>LSO 魔数（两字节，不是 u16 数值）。</summary>
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x00, 0xBF };

    /// <summary>固定块 <c>"TCSO" 00 04 00 00 00 00</c>。</summary>
    public static ReadOnlySpan<byte> TagBlock => new byte[] { 0x54, 0x43, 0x53, 0x4F, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>名字与版本字节之间的固定 padding 长度。</summary>
    public const int NamePaddingLength = 3;

    /// <summary>每个属性的尾随分隔字节。见类型注释里的实测说明。</summary>
    public const byte PropertyTerminator = 0x00;

    /// <summary><c>00 BF</c> + <c>u32</c> 长度字段，不计入 payload length。</summary>
    public const int PrefixLength = 6;

    private readonly List<KeyValuePair<string, AmfValue>> _properties = new();

    public SolFile(string name, SolFormat format = SolFormat.Amf0)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Format = format;
    }

    /// <summary>存档名，通常等于 <c>.sol</c> 文件名去掉扩展名（如 <c>settings</c>、<c>pvz</c>）。</summary>
    public string Name { get; set; }

    public SolFormat Format { get; set; }

    /// <summary>属性表。保持原顺序；允许重名（两种格式都是序列，不是映射）。
    /// 值的具体类型由 <see cref="Format"/> 决定：AMF0 是 <see cref="Amf0Value"/>，AMF3 是 <see cref="Amf3Value"/>。</summary>
    public IReadOnlyList<KeyValuePair<string, AmfValue>> Properties => _properties;

    /// <summary>追加一个属性（写回时排在最后）。</summary>
    public SolFile Add(string name, AmfValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _properties.Add(new KeyValuePair<string, AmfValue>(name, value));
        return this;
    }

    /// <summary>解析文件头。AMF3 文件也能解析头 —— 只有正文需要版本相关的读取器。</summary>
    /// <exception cref="InvalidDataException">魔数 / 固定块 / padding / 长度字段不符。</exception>
    public static SolHeader ReadHeader(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // 最小头：前缀 6 + 固定块 10 + 名字长度 2 + 至少 1 字节名字 + padding 3 + 版本 1
        if (bytes.Length < PrefixLength + TagBlock.Length + 2 + 1 + NamePaddingLength + 1)
        {
            throw new InvalidDataException($"Buffer is too short ({bytes.Length} bytes) to hold an LSO header.");
        }

        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                $"Not an LSO file: expected magic 00 BF, found {bytes[0]:x2} {bytes[1]:x2}.");
        }

        if (!bytes.AsSpan(PrefixLength, TagBlock.Length).SequenceEqual(TagBlock))
        {
            throw new InvalidDataException(
                $"Unexpected LSO tag block at offset {PrefixLength}: expected \"TCSO\" 00 04 00 00 00 00, " +
                $"found {Convert.ToHexString(bytes.AsSpan(PrefixLength, TagBlock.Length))}.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4));
        if (declaredLength != bytes.Length - PrefixLength)
        {
            throw new InvalidDataException(
                $"LSO length field says {declaredLength} bytes of payload, but the file holds " +
                $"{bytes.Length - PrefixLength} (file size {bytes.Length}).");
        }

        var nameLengthOffset = PrefixLength + TagBlock.Length;
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(nameLengthOffset, 2));
        if (nameLength == 0)
        {
            throw new InvalidDataException("LSO name is empty.");
        }

        var nameOffset = nameLengthOffset + 2;
        var paddingOffset = nameOffset + nameLength;
        var versionOffset = paddingOffset + NamePaddingLength;

        if (versionOffset >= bytes.Length)
        {
            throw new InvalidDataException(
                $"LSO name length {nameLength} at offset {nameLengthOffset} runs past the end of the buffer.");
        }

        var padding = bytes.AsSpan(paddingOffset, NamePaddingLength);
        if (!padding.SequenceEqual(stackalloc byte[NamePaddingLength]))
        {
            throw new InvalidDataException(
                $"Expected 3 zero bytes between the LSO name and the version byte, found " +
                $"{Convert.ToHexString(padding)}.");
        }

        var name = Encoding.UTF8.GetString(bytes, nameOffset, nameLength);
        var version = bytes[versionOffset];

        if (!Enum.IsDefined(typeof(SolFormat), version))
        {
            throw new InvalidDataException(
                $"Unknown LSO AMF version 0x{version:x2} at offset {versionOffset}; expected 0x00 (AMF0) or 0x03 (AMF3).");
        }

        return new SolHeader(name, (SolFormat)version, declaredLength, versionOffset + 1);
    }

    /// <summary>
    /// 解析一个 LSO 文件。正文按头部声明的版本分派到 <see cref="Amf0Reader"/> 或 <see cref="Amf3Reader"/> ——
    /// 两种版本的值模型不通用，所以这里必须显式分派，不能「按 AMF0 硬读」。
    /// </summary>
    public static SolFile Read(byte[] bytes)
    {
        var header = ReadHeader(bytes);
        var file = new SolFile(header.Name, header.Format);

        switch (header.Format)
        {
            case SolFormat.Amf0:
                ReadAmf0Body(bytes, header, file);
                break;

            case SolFormat.Amf3:
                ReadAmf3Body(bytes, header, file);
                break;

            default:
                throw new NotSupportedException(
                    $"'{header.Name}' declares AMF version 0x{(byte)header.Format:x2} ({header.Format}), " +
                    "which has no body reader.");
        }

        return file;
    }

    public static SolFile ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>序列化回 LSO 字节。对未经修改的 <see cref="Read"/> 结果，输出应与原文件字节级相同。</summary>
    public byte[] Write()
    {
        var nameBytes = EncodeUtf8(Name, nameof(Name));
        if (nameBytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"LSO name is {nameBytes.Length} bytes; the field holds at most {ushort.MaxValue}.");
        }

        var body = new ArrayBufferWriter<byte>();

        switch (Format)
        {
            case SolFormat.Amf0:
                WriteAmf0Body(body);
                break;

            case SolFormat.Amf3:
                WriteAmf3Body(body);
                break;

            default:
                throw new NotSupportedException(
                    $"Writing AMF version 0x{(byte)Format:x2} ({Format}) is not supported.");
        }

        var headerLength = PrefixLength
            + TagBlock.Length
            + 2
            + nameBytes.Length
            + NamePaddingLength
            + 1;
        var totalLength = headerLength + body.WrittenCount;

        var output = new ArrayBufferWriter<byte>(totalLength);
        output.Write(Magic);
        Amf0Writer.WriteUInt32(output, checked((uint)(totalLength - PrefixLength)));
        output.Write(TagBlock);
        Amf0Writer.WriteUInt16(output, (ushort)nameBytes.Length);
        output.Write(nameBytes);
        output.Write(stackalloc byte[NamePaddingLength]);
        Amf0Writer.WriteByte(output, (byte)Format);
        output.Write(body.WrittenSpan);

        return output.WrittenSpan.ToArray();
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());

    private static void ReadAmf0Body(byte[] bytes, SolHeader header, SolFile file)
    {
        var reader = new Amf0Reader(bytes, header.BodyOffset);

        while (!reader.AtEnd)
        {
            var propertyOffset = reader.Position;
            var name = reader.ReadShortString();
            EnsurePropertyNameIsUsable(name, propertyOffset);

            var value = reader.ReadValue();
            var terminator = reader.ReadByte();

            if (terminator != PropertyTerminator)
            {
                throw new InvalidDataException(
                    $"LSO property '{name}' (offset {propertyOffset}) is not followed by the expected 0x00 " +
                    $"separator byte; found 0x{terminator:x2}.");
            }

            file._properties.Add(new KeyValuePair<string, AmfValue>(name, value));
        }
    }

    private static void ReadAmf3Body(byte[] bytes, SolHeader header, SolFile file)
    {
        var reader = new Amf3Reader(bytes, header.BodyOffset);

        while (!reader.AtEnd)
        {
            var propertyOffset = reader.Position;
            var name = reader.ReadName("LSO property name");
            EnsurePropertyNameIsUsable(name, propertyOffset);

            var value = reader.ReadValue();
            var terminator = reader.ReadByte();

            if (terminator != PropertyTerminator)
            {
                throw new InvalidDataException(
                    $"LSO property '{name}' (offset {propertyOffset}) is not followed by the expected 0x00 " +
                    $"separator byte; found 0x{terminator:x2}.");
            }

            file._properties.Add(new KeyValuePair<string, AmfValue>(name, value));
        }
    }

    private static void EnsurePropertyNameIsUsable(string name, int propertyOffset)
    {
        if (name.Length == 0)
        {
            throw new InvalidDataException(
                $"LSO property at offset {propertyOffset} has an empty name. The body is a flat run of " +
                "properties with no end marker, so a zero-length name would make the stream ambiguous.");
        }
    }

    private void WriteAmf0Body(ArrayBufferWriter<byte> body)
    {
        foreach (var (key, value) in _properties)
        {
            if (value is not Amf0Value amf0Value)
            {
                throw new InvalidOperationException(
                    $"Property '{key}' holds a {value.GetType().Name}, but this file is declared as AMF0 " +
                    "(0x00). Mixing formats would produce a file neither Flash nor we could read back.");
            }

            var keyBytes = EncodeUtf8(key, "property name");
            if (keyBytes.Length is 0 or > ushort.MaxValue)
            {
                throw new InvalidOperationException($"LSO property name must be 1..{ushort.MaxValue} bytes, got {keyBytes.Length}.");
            }

            Amf0Writer.WriteUInt16(body, (ushort)keyBytes.Length);
            body.Write(keyBytes);
            Amf0Writer.Write(body, amf0Value);
            Amf0Writer.WriteByte(body, PropertyTerminator);
        }
    }

    private void WriteAmf3Body(ArrayBufferWriter<byte> body)
    {
        // 引用表跟着整个正文走，所以写出器要在这里创建一次、全程复用。
        var writer = new Amf3Writer(body);

        foreach (var (key, value) in _properties)
        {
            if (value is not Amf3Value amf3Value)
            {
                throw new InvalidOperationException(
                    $"Property '{key}' holds a {value.GetType().Name}, but this file is declared as AMF3 " +
                    "(0x03). Mixing formats would produce a file neither Flash nor we could read back.");
            }

            if (key.Length == 0)
            {
                throw new InvalidOperationException("LSO property name must not be empty.");
            }

            writer.WriteName(key);
            writer.WriteValue(amf3Value);
            writer.WriteByte(PropertyTerminator);
        }
    }

    private static byte[] EncodeUtf8(string value, string what) =>
        value is null
            ? throw new ArgumentNullException(nameof(value), $"{what} is null")
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(value);
}
