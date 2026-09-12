using System.Text;
using FlashBrowserForMacOS.Sol;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// <see cref="Amf3Reader"/> / <see cref="Amf3Writer"/> 的测试。
///
/// 三个 AMF3 fixture 只用到 Object / Array / Integer / Boolean / String 五种标记，
/// 所以其余类型（Vector、Dictionary、ByteArray、Date、XML、引用表）用**合成值**补齐。
///
/// <para>判据统一是「写 → 读 → 再写」两次字节完全相同。对 AMF3 来说这条判据尤其关键 ——
/// 它同时覆盖了三张引用表的入表时机（哪些字符串/对象/traits 会进表），
/// 而引用下标一旦算错，字节级比对立刻会红。</para>
/// </summary>
public class Amf3RoundTripTests
{
    [Fact]
    public void EverySupportedValueShape_RoundTripsThroughBytes()
    {
        var cases = new (string Label, Amf3Value Value)[]
        {
            ("undefined", new Amf3Undefined()),
            ("null", new Amf3Null()),
            ("false", new Amf3Boolean(false)),
            ("true", new Amf3Boolean(true)),
            ("integer zero", new Amf3Integer(0)),
            ("integer negative", new Amf3Integer(-1)),
            ("integer max", new Amf3Integer(Amf3Integer.MaxValue)),
            ("integer min", new Amf3Integer(Amf3Integer.MinValue)),
            ("double zero", new Amf3Double(0)),
            ("double negative", new Amf3Double(-2.5)),
            ("double NaN", new Amf3Double(double.NaN)),
            ("string empty", new Amf3String(string.Empty)),
            ("string ascii", new Amf3String("saveData")),
            ("string non-ascii", new Amf3String("中文值 · 4399")),
            ("xml document", new Amf3XmlDocument("<root a=\"1\"/>")),
            ("xml", new Amf3Xml("<e4x/>")),
            ("date", new Amf3Date(1234567890.0)),
            ("byte array empty", new Amf3ByteArray(Array.Empty<byte>())),
            ("byte array", new Amf3ByteArray(new byte[] { 0x00, 0xFF, 0x7F })),
            ("array empty", new Amf3Array()),
            ("array dense", DenseArray(Ints(1, 2, 3))),
            ("array with associative part", ArrayWithAssociative(("count", new Amf3Integer(2)))),
            ("vector int", VectorInt(false, 1, -2, 3)),
            ("vector uint", VectorUint(true, 0, uint.MaxValue)),
            ("vector double", VectorDouble(false, 1.5, -0.5)),
            ("vector object", VectorObject("int", false, new Amf3Integer(1), new Amf3String("two"))),
            ("dictionary", Dictionary(false, (new Amf3String("k"), new Amf3Integer(1)))),
            ("object anonymous dynamic", DynamicObject()),
            ("object sealed", SealedObject("x", new Amf3Integer(5))),
            ("object anonymous empty", new Amf3Object(AnonymousTraits(dynamic: false))),
        };

        foreach (var (label, value) in cases)
        {
            AssertRoundTrips(value, label);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x7Fu)]
    [InlineData(0x80u)]
    [InlineData(0x3FFFu)]
    [InlineData(0x4000u)]
    [InlineData(0x1FFFFFu)]
    [InlineData(0x200000u)]
    [InlineData(0x1FFFFFFFu)]
    public void U29_RoundTripsAtEveryEncodingBoundary(uint raw)
    {
        var bytes = new byte[4];
        var written = WriteU29(raw);
        Assert.True(written.Length is >= 1 and <= 4);

        // 手写读出，确认 1/2/3/4 字节形态在边界上都读得回来。
        var reader = new Amf3Reader(written);
        Assert.Equal(raw, reader.ReadU29());
        Assert.True(reader.AtEnd, $"U29 {raw} should use exactly {written.Length} byte(s)");

        // 顺带覆盖「包在 Integer 值里」的路径，避免 29 位补码与 U29 编码互相掩盖错误。
        var value = new Amf3Integer(unchecked((int)(raw << 3) >> 3));
        AssertRoundTrips(value, $"integer {value.Value}");
    }

    [Fact]
    public void U29_WriterPicksTheShortestForm()
    {
        Assert.Equal(new byte[] { 0x7F }, WriteU29(0x7F));
        Assert.Equal(new byte[] { 0x81, 0x00 }, WriteU29(0x80));
        Assert.Equal(new byte[] { 0x81, 0x7F }, WriteU29(0xFF));

        // 2 字节形态的上界：0x3FFF = 0b11_1111_1111_1111 → 0x80|0x7F, 0x7F。
        Assert.Equal(new byte[] { 0xFF, 0x7F }, WriteU29(0x3FFF));

        // 第四个字节贡献完整 8 位，所以 0x200000 的首字节高位是 0（v>>22 == 0）、末字节是 0x00。
        Assert.Equal(new byte[] { 0x80, 0xC0, 0x80, 0x00 }, WriteU29(0x200000));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, WriteU29(0x1FFFFFFF));
    }

    [Fact]
    public void Read_Object_UsesTheSharedObjectAndTraitsU29()
    {
        // 0x0A = Object；0x0B = 0b1011：
        //   bit0=1 对象内联、bit1=1 traits 内联、bit2=0 非 externalizable、bit3=1 dynamic、bit4起=0 密封成员
        // 0x01 = 空类名（U29S 内联长度 0）；再一个 0x01 = 空名，动态成员结束。
        // 这正是 pvz.sol 里 saveData 对象的前几个字节。
        byte[] bytes = { 0x0A, 0x0B, 0x01, 0x01 };

        var obj = Assert.IsType<Amf3Object>(new Amf3Reader(bytes).ReadValue());

        Assert.Equal(string.Empty, obj.Traits.ClassName.Value);
        Assert.True(obj.Traits.Dynamic);
        Assert.False(obj.Traits.Externalizable);
        Assert.Empty(obj.Traits.SealedMemberNames);
        Assert.Empty(obj.DynamicMembers);
        Assert.Null(obj.TraitsReferenceIndex);

        Assert.Equal(bytes, Amf3Writer.Write(obj));
    }

    [Fact]
    public void Read_Object_ReadsSealedMembersByName()
    {
        // 密封成员数 1、非动态：u29 = 1 | (1<<1) | (1<<4) = 0x13。
        // 0x01 空类名、0x03 'x' 成员名、0x04 0x05 整数 5。
        byte[] bytes = { 0x0A, 0x13, 0x01, 0x03, (byte)'x', 0x04, 0x05 };

        var obj = Assert.IsType<Amf3Object>(new Amf3Reader(bytes).ReadValue());

        Assert.False(obj.Traits.Dynamic);
        Assert.Equal(new[] { "x" }, obj.Traits.SealedMemberNames.Select(n => n.Value).ToArray());
        Assert.Equal(5, Assert.IsType<Amf3Integer>(Assert.Single(obj.SealedValues)).Value);
        Assert.Equal(new[] { "x" }, obj.EnumerateMembers().Select(m => m.Key).ToArray());

        Assert.Equal(bytes, Amf3Writer.Write(obj));
    }

    [Fact]
    public void Read_And_Write_PreserveTraitReferences()
    {
        var traits = AnonymousTraits(dynamic: true);
        var first = new Amf3Object(traits);
        var second = new Amf3Object(traits) { TraitsReferenceIndex = 0 };

        var array = new Amf3Array();
        array.DenseItems.Add(first);
        array.DenseItems.Add(second);

        var written = Amf3Writer.Write(array);
        var parsed = Assert.IsType<Amf3Array>(new Amf3Reader(written).ReadValue());

        var parsedFirst = Assert.IsType<Amf3Object>(parsed.DenseItems[0]);
        var parsedSecond = Assert.IsType<Amf3Object>(parsed.DenseItems[1]);

        Assert.Null(parsedFirst.TraitsReferenceIndex);
        Assert.Equal(0, parsedSecond.TraitsReferenceIndex);

        // 引用形态也要能解析成实际 traits，供上层查看。
        Assert.True(parsedSecond.Traits.Dynamic);

        Assert.Equal(written, Amf3Writer.Write(parsed));
    }

    [Fact]
    public void Read_ObjectReference_ResolvesThroughTheObjectTable()
    {
        // 数组两个元素：第一个是内联对象（占对象表 0 号），第二个指向它。
        var obj = new Amf3Object(AnonymousTraits(dynamic: false));

        var array = new Amf3Array();
        array.DenseItems.Add(obj);
        array.DenseItems.Add(new Amf3ObjectReference(0));

        var written = Amf3Writer.Write(array);
        var parsed = Assert.IsType<Amf3Array>(new Amf3Reader(written).ReadValue());

        var reference = Assert.IsType<Amf3ObjectReference>(parsed.DenseItems[1]);
        Assert.Equal(0, reference.Index);
    }

    [Fact]
    public void Read_StringReference_IsResolvedAndKeptAsAReference()
    {
        // 0x06 0x07 'a' 'b' 'c'  → 内联 "abc"（U29S 长度 3 = (3 << 1) | 1），进入字符串表 0 号
        // 0x06 0x00              → 字符串引用 0 号
        byte[] bytes = { 0x06, 0x07, (byte)'a', (byte)'b', (byte)'c', 0x06, 0x00 };

        var reader = new Amf3Reader(bytes);
        var inline = Assert.IsType<Amf3String>(reader.ReadValue());
        var referenced = Assert.IsType<Amf3String>(reader.ReadValue());

        Assert.Null(inline.ReferenceIndex);
        Assert.Equal(0, referenced.ReferenceIndex);
        Assert.Equal("abc", referenced.Value);
    }

    [Fact]
    public void Read_EmptyStrings_DoNotEnterTheStringTable()
    {
        // 0x06 0x01 → 内联空串。规范说空串「从不按引用发送」，所以它不该占字符串表位置。
        // 于是紧随其后的「引用 0 号」必须是越界 —— 这正好把这条规则钉住。
        byte[] bytes = { 0x06, 0x01, 0x06, 0x00 };

        var reader = new Amf3Reader(bytes);
        Assert.Equal(string.Empty, Assert.IsType<Amf3String>(reader.ReadValue()).Value);

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadValue());
        Assert.Contains("out of range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsOnOutOfRangeObjectReference()
    {
        // 数组稠密部分 2 个元素：第一个是 Null，第二个写成对象引用 5 号
        // （对象标记 0x0A + U29 = (5 << 1) | 0，最低位 0 表示这是引用而不是内联对象）。
        // 此时对象表里只有数组自己（0 号），5 号越界。
        byte[] bytes = { 0x09, 0x05, 0x01, 0x01, 0x0A, 0x0A };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("out of range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsOnExternalizableObject()
    {
        // bit2=1 externalizable：u29 = 1 | (1<<1) | (1<<2) = 0x07；随后是空类名。
        byte[] bytes = { 0x0A, 0x07, 0x01, 0x01 };

        var exception = Assert.Throws<NotSupportedException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("externalizable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsOnInvalidUtf8InsteadOfReplacingBytes()
    {
        // 0x06 = String，0x05 = U29S 内联长度 2（(2 << 1) | 1），随后是两个孤立续字节 0x80 0x80。
        byte[] bytes = { 0x06, 0x05, 0x80, 0x80 };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ThrowsOnNestingDeeperThanTheLimit()
    {
        // 每一层是一个「稠密部分 1 个元素、关联部分 0 个」的数组：0x09 0x03 0x01。
        var bytes = new List<byte>();
        for (var i = 0; i < Amf3Reader.MaxDepth + 20; i++)
        {
            bytes.Add(0x09);
            bytes.Add(0x03);
            bytes.Add(0x01);
        }

        bytes.Add(0x01); // 最里层放 Null

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes.ToArray()).ReadValue());

        Assert.Contains("nesting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_ThrowsWhenStreamEndsEarly()
    {
        // 声明 3 个元素的稠密数组，却只给 1 个。
        byte[] bytes = { 0x09, 0x07, 0x01, 0x04, 0x00 };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("ended early", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsCountLargerThanTheRemainingBytes()
    {
        // 稠密部分声明 4672 个元素（U29 = 0xC9 0x01 = (4672 << 1) | 1），但缓冲里只剩几个字节 ——
        // 必须在按这个数字分配之前就拒绝。注意最低位必须是 1，否则会被当成对象引用。
        byte[] bytes = { 0x09, 0xC9, 0x01, 0x01 };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("declares", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsUndefinedMarker()
    {
        byte[] bytes = { 0x12 };

        var exception = Assert.Throws<InvalidDataException>(() => new Amf3Reader(bytes).ReadValue());

        Assert.Contains("not a defined AMF3 type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsIntegerOutsideTheTwentyNineBitRange()
    {
        var tooBig = new Amf3Integer(Amf3Integer.MaxValue + 1);

        Assert.Throws<InvalidOperationException>(() => Amf3Writer.Write(tooBig));
    }

    [Fact]
    public void Write_RejectsReferenceIndexThatIsOutOfRange()
    {
        var value = new Amf3Array();
        value.DenseItems.Add(new Amf3ObjectReference(7));

        Assert.Throws<InvalidOperationException>(() => Amf3Writer.Write(value));
    }

    [Fact]
    public void Write_CountsNamesTowardTheStringTable()
    {
        // 容器层属性名与容器内的值是同一张字符串表 —— 名字先入表，所以后面可以引用 0 号。
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new Amf3Writer(buffer);

        writer.WriteName("duplicate");
        writer.WriteValue(new Amf3String("duplicate") { ReferenceIndex = 0 });

        var bytes = buffer.WrittenSpan.ToArray();

        // 名字是内联的 "duplicate"（U29S 长度 9 → 0x13），值是引用 0 号（0x00）。
        var expected = new List<byte> { 0x13 };
        expected.AddRange(Encoding.ASCII.GetBytes("duplicate"));
        expected.Add(0x06);
        expected.Add(0x00);

        Assert.Equal(expected.ToArray(), bytes);

        // 读回来也要解成同一个字符串。
        var reader = new Amf3Reader(bytes);
        Assert.Equal("duplicate", reader.ReadName("property name"));
        Assert.Equal("duplicate", Assert.IsType<Amf3String>(reader.ReadValue()).Value);
    }

    [Fact]
    public void WriteName_RejectsReferencedNames()
    {
        // 名字位置出现引用必须显式报错：模型里没地方存那个下标，展开成字面量会改变写回的字节。
        // 0x00 = U29S 引用 0 号。
        byte[] bytes = { 0x00 };

        var exception = Assert.Throws<NotSupportedException>(() => new Amf3Reader(bytes).ReadName("property name"));

        Assert.Contains("reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_AcceptsNonMinimalU29_ButTheWriterCanonicalisesIt()
    {
        // 已知边界：读取器接受非最紧凑的 U29，写出器总是写最紧凑形式。
        // 所以「原样读进来再写出去」对**别的工具写的**非规范 U29 不保证逐字节相同。
        // Flash 自己写的 .sol 全是紧凑形式（4 个 fixture 已覆盖），这条只影响外来文件。
        byte[] padded = { 0x04, 0x80, 0x00 }; // 整数标记 + 两字节形式的 U29 0

        var value = Assert.IsType<Amf3Integer>(new Amf3Reader(padded).ReadValue());
        Assert.Equal(0, value.Value);

        Assert.Equal(new byte[] { 0x04, 0x00 }, Amf3Writer.Write(value));
    }

    private static void AssertRoundTrips(Amf3Value value, string label)
    {
        var written = Amf3Writer.Write(value);
        var parsed = new Amf3Reader(written).ReadValue();

        Assert.Equal(value.Type, parsed.Type);

        var rewritten = Amf3Writer.Write(parsed);
        if (!written.AsSpan().SequenceEqual(rewritten))
        {
            Assert.Fail(
                $"AMF3 round-trip failed for '{label}'.{Environment.NewLine}" +
                $"  written   ({written.Length} B): {Convert.ToHexString(written.AsSpan(0, Math.Min(48, written.Length)))}{Environment.NewLine}" +
                $"  rewritten ({rewritten.Length} B): {Convert.ToHexString(rewritten.AsSpan(0, Math.Min(48, rewritten.Length)))}");
        }
    }

    private static byte[] WriteU29(uint value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        new Amf3Writer(buffer).WriteU29(value);
        return buffer.WrittenSpan.ToArray();
    }

    private static Amf3Traits AnonymousTraits(bool dynamic) => new(new Amf3String(string.Empty), false, dynamic);

    private static Amf3Value[] Ints(params int[] values)
    {
        var items = new Amf3Value[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            items[i] = new Amf3Integer(values[i]);
        }

        return items;
    }

    private static Amf3Array DenseArray(params Amf3Value[] items)
    {
        var array = new Amf3Array();
        array.DenseItems.AddRange(items);
        return array;
    }

    private static Amf3Array ArrayWithAssociative(params (string Key, Amf3Value Value)[] members)
    {
        var array = new Amf3Array();
        foreach (var (key, value) in members)
        {
            array.AssociativeMembers.Add(new KeyValuePair<Amf3String, Amf3Value>(new Amf3String(key), value));
        }

        return array;
    }

    private static Amf3Object DynamicObject()
    {
        var obj = new Amf3Object(AnonymousTraits(dynamic: true));
        obj.DynamicMembers.Add(new KeyValuePair<Amf3String, Amf3Value>(new Amf3String("level"), new Amf3Integer(6)));
        obj.DynamicMembers.Add(new KeyValuePair<Amf3String, Amf3Value>(new Amf3String("on"), new Amf3Boolean(true)));
        return obj;
    }

    private static Amf3Object SealedObject(string memberName, Amf3Value value)
    {
        var traits = AnonymousTraits(dynamic: false);
        traits.SealedMemberNames.Add(new Amf3String(memberName));

        var obj = new Amf3Object(traits);
        obj.SealedValues.Add(value);
        return obj;
    }

    private static Amf3VectorInt VectorInt(bool fixedLength, params int[] items)
    {
        var vector = new Amf3VectorInt { FixedLength = fixedLength };
        vector.Items.AddRange(items);
        return vector;
    }

    private static Amf3VectorUint VectorUint(bool fixedLength, params uint[] items)
    {
        var vector = new Amf3VectorUint { FixedLength = fixedLength };
        vector.Items.AddRange(items);
        return vector;
    }

    private static Amf3VectorDouble VectorDouble(bool fixedLength, params double[] items)
    {
        var vector = new Amf3VectorDouble { FixedLength = fixedLength };
        vector.Items.AddRange(items);
        return vector;
    }

    private static Amf3VectorObject VectorObject(string elementTypeName, bool fixedLength, params Amf3Value[] items)
    {
        var vector = new Amf3VectorObject
        {
            FixedLength = fixedLength,
            ElementTypeName = new Amf3String(elementTypeName),
        };

        vector.Items.AddRange(items);
        return vector;
    }

    private static Amf3Dictionary Dictionary(bool weakKeys, params (Amf3Value Key, Amf3Value Value)[] entries)
    {
        var dictionary = new Amf3Dictionary { WeakKeys = weakKeys };
        foreach (var (key, value) in entries)
        {
            dictionary.Entries.Add(new KeyValuePair<Amf3Value, Amf3Value>(key, value));
        }

        return dictionary;
    }
}
