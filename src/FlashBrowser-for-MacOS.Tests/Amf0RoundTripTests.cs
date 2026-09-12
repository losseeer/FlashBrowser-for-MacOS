using System.Text;
using FlashBrowserForMacOS.Sol;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// <see cref="Amf0Reader"/> / <see cref="Amf0Writer"/> 的 round-trip 测试。
///
/// 唯一的 AMF0 fixture（<c>settings.sol</c>）只用到 Number / Boolean / String / Object 四种标记，
/// 所以这里用**合成值**补齐其余类型。判据统一是：
/// <c>写 → 读 → 再写</c> 两次字节完全相同。这比逐字段比对更强 —— 它同时覆盖了
/// 长度字段形态（短串 / 长串）、成员顺序、以及声明的元素个数是否被原样保留。
/// </summary>
public class Amf0RoundTripTests
{
    [Fact]
    public void EverySupportedValueShape_RoundTripsThroughBytes()
    {
        var cases = new (string Label, Amf0Value Value)[]
        {
            ("number 0", new Amf0Number(0)),
            ("number -1.5", new Amf0Number(-1.5)),
            ("number max", new Amf0Number(double.MaxValue)),
            ("number NaN", new Amf0Number(double.NaN)),
            ("boolean true", new Amf0Boolean(true)),
            ("boolean false", new Amf0Boolean(false)),
            ("string empty", new Amf0String(string.Empty)),
            ("string ascii", new Amf0String("allowThirdPartyLSOAccess")),
            ("string non-ascii", new Amf0String("中文键值 · 4399")),
            ("long string", new Amf0LongString(new string('x', 70000))),
            ("xml document", new Amf0XmlDocument("<root attr=\"1\"/>")),
            ("null", new Amf0Null()),
            ("undefined", new Amf0Undefined()),
            ("reference", new Amf0Reference(42)),
            ("date", new Amf0Date(1234567890.0, 0)),
            ("date with timezone", new Amf0Date(-1.0, -480)),
            ("object empty", new Amf0Object()),
            ("typed object empty", new Amf0TypedObject("com.example.Foo")),
            ("ecma array empty", new Amf0EcmaArray(0)),
            ("strict array empty", new Amf0StrictArray()),
            ("strict array mixed", StrictArray(
                new Amf0Number(1),
                new Amf0String("two"),
                new Amf0Null(),
                new Amf0Boolean(false))),
            ("object nested", Object(
                ("outer", Object(("inner", new Amf0Number(7)))))),

            // 声明个数与实际成员数故意不一致 —— 规范允许，写回时必须原样保留声明值。
            ("ecma array stale count", EcmaArray(9, ("a", new Amf0Number(1)), ("b", new Amf0Number(2)))),

            // 键名重复 + 顺序非常规：AMF0 是有序序列而非映射，归并成字典就会在这里露馅。
            ("object duplicate keys", Object(
                ("k", new Amf0Number(1)),
                ("k", new Amf0Number(2)),
                ("a", new Amf0Number(3)))),
        };

        foreach (var (label, value) in cases)
        {
            AssertRoundTrips(value, label);
        }
    }

    [Fact]
    public void Read_ConsumesExactlyTheWrittenBytes()
    {
        var value = Object(
            ("number", new Amf0Number(3.5)),
            ("text", new Amf0String("tail")),
            ("flag", new Amf0Boolean(true)));

        var bytes = Amf0Writer.Write(value);
        var reader = new Amf0Reader(bytes);

        reader.ReadValue();

        Assert.True(reader.AtEnd, $"reader stopped at {reader.Position} of {bytes.Length} bytes");
    }

    [Fact]
    public void Read_Object_PreservesMemberOrderAndDuplicates()
    {
        var bytes = Amf0Writer.Write(Object(
            ("z", new Amf0String("first")),
            ("m", new Amf0String("second")),
            ("a", new Amf0String("third"))));

        var parsed = Assert.IsType<Amf0Object>(new Amf0Reader(bytes).ReadValue());

        Assert.Equal(new[] { "z", "m", "a" }, parsed.Members.Select(m => m.Key).ToArray());
    }

    [Theory]
    [InlineData(0x04, "MovieClip")]
    [InlineData(0x0D, "Unsupported")]
    [InlineData(0x0E, "Recordset")]
    [InlineData(0x11, "AvmPlus")]
    public void Read_ThrowsOnMarkersWithoutDefinedPayload(byte marker, string expectedNameInMessage)
    {
        byte[] bytes = { marker };

        var exception = Assert.Throws<NotSupportedException>(() => new Amf0Reader(bytes).ReadValue());

        Assert.Contains(expectedNameInMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ThrowsOnBareObjectEndMarker()
    {
        // 0x09 是对象结束标记，不是值。
        byte[] bytes = { (byte)Amf0Type.ObjectEnd };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf0Reader(bytes).ReadValue());

        Assert.Contains("terminator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsWhenStreamEndsEarly()
    {
        var bytes = Amf0Writer.Write(new Amf0String("truncated"));

        var exception = Assert.Throws<InvalidDataException>(
            () => new Amf0Reader(bytes[..(bytes.Length - 3)]).ReadValue());

        Assert.Contains("ended early", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsOnObjectWithoutEndMarker()
    {
        // 0x03 之后直接是结束标记的第一个字节，但少了 0x09。
        byte[] bytes = { (byte)Amf0Type.Object, 0x00, 0x01, (byte)'k', (byte)Amf0Type.Null, 0x00, 0x00, 0x7F };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf0Reader(bytes).ReadValue());

        Assert.Contains("end marker", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsOnInvalidUtf8InsteadOfReplacingBytes()
    {
        // u16 长度 2 + 两个孤立续字节 0x80 0x80 —— 不是合法 UTF-8。
        byte[] bytes = { (byte)Amf0Type.String, 0x00, 0x02, 0x80, 0x80 };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf0Reader(bytes).ReadValue());

        Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_RejectsShortStringLongerThanItsLengthField()
    {
        var huge = new Amf0String(new string('x', ushort.MaxValue + 1));

        Assert.Throws<OverflowException>(() => Amf0Writer.Write(huge));
    }

    [Fact]
    public void WriteShortString_MatchesHandBuiltBytes()
    {
        // 用一条手写字节独立校验写出器，而不是只让读写互相对拍。
        var expected = new byte[]
        {
            (byte)Amf0Type.String, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c',
        };

        Assert.Equal(expected, Amf0Writer.Write(new Amf0String("abc")));
    }

    [Fact]
    public void WriteNumber_IsBigEndian()
    {
        // AMF0 规范：双精度按大端写。1.0 应为 3F F0 00 00 00 00 00 00。
        var expected = new byte[]
        {
            (byte)Amf0Type.Number, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        Assert.Equal(expected, Amf0Writer.Write(new Amf0Number(1.0)));
    }

    [Fact]
    public void ObjectEndMarker_IsWrittenAsZeroZeroZeroNine()
    {
        var bytes = Amf0Writer.Write(new Amf0Object());

        Assert.Equal(new byte[] { (byte)Amf0Type.Object, 0x00, 0x00, 0x09 }, bytes);
    }

    private static void AssertRoundTrips(Amf0Value value, string label)
    {
        var written = Amf0Writer.Write(value);
        var parsed = new Amf0Reader(written).ReadValue();

        Assert.Equal(value.Type, parsed.Type);

        var rewritten = Amf0Writer.Write(parsed);
        if (!written.AsSpan().SequenceEqual(rewritten))
        {
            Assert.Fail(
                $"AMF0 round-trip failed for '{label}'.{Environment.NewLine}" +
                $"  written   ({written.Length} B): {Convert.ToHexString(written.AsSpan(0, Math.Min(48, written.Length)))}{Environment.NewLine}" +
                $"  rewritten ({rewritten.Length} B): {Convert.ToHexString(rewritten.AsSpan(0, Math.Min(48, rewritten.Length)))}");
        }
    }

    private static Amf0Object Object(params (string Key, Amf0Value Value)[] members)
    {
        var obj = new Amf0Object();
        foreach (var (key, value) in members)
        {
            obj.Members.Add(new KeyValuePair<string, Amf0Value>(key, value));
        }

        return obj;
    }

    private static Amf0EcmaArray EcmaArray(uint declaredCount, params (string Key, Amf0Value Value)[] members)
    {
        var array = new Amf0EcmaArray(declaredCount);
        foreach (var (key, value) in members)
        {
            array.Members.Add(new KeyValuePair<string, Amf0Value>(key, value));
        }

        return array;
    }

    private static Amf0StrictArray StrictArray(params Amf0Value[] items)
    {
        var array = new Amf0StrictArray();
        array.Items.AddRange(items);
        return array;
    }
}
