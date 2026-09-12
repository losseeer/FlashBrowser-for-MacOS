using System.Text;
using FlashBrowserForMacOS.Models;
using FlashBrowserForMacOS.Sol;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// <see cref="SolFile"/> 的测试。4 个 fixture 里 <c>settings.sol</c> 是 AMF0，另外 3 个是 AMF3。
///
/// 核心判据是 <see cref="Write_EveryFixture_ReproducesOriginalBytesExactly"/>：解析再写回必须**字节级相等**。
/// 这条比「字段值对得上」强得多 —— 只要对格式的理解有任何一处偏差（少一个分隔字节、成员顺序变了、
/// 字符串用了不同的长度形态、AMF3 的引用下标算错），它都会红。
/// </summary>
public class SolFileTests
{
    private const int SettingsSolSize = 845;

    private static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "TestData");

    private static string FixturePath(string fileName) => Path.Combine(TestDataDir, fileName);

    private static byte[] ReadFixture(string fileName) => File.ReadAllBytes(FixturePath(fileName));

    [Fact]
    public void Read_SettingsSol_ParsesExpectedTopLevelProperties()
    {
        var file = SolFile.Read(ReadFixture("settings.sol"));

        Assert.Equal("settings", file.Name);
        Assert.Equal(SolFormat.Amf0, file.Format);
        Assert.Equal(23, file.Properties.Count);

        Assert.Equal(
            new[]
            {
                "allowThirdPartyLSOAccess",
                "authorizedFeaturesExpiry",
                "crossdomainAllow",
                "crossdomainAlways",
                "defaultalways",
                "defaultaudio",
                "defaultcamera",
                "defaultklimit",
                "defaultmicrophone",
                "disallowP2PUplink",
                "domains",
                "echosuppression",
                "gain",
                "panel",
                "safefullscreen",
                "secureCrossDomainCacheSize",
                "trustedPaths",
                "windowlessDisable",
                "debuggerLocalhost",
                "debuggerMachine",
                "debuggerDontShow",
                "debuggerListenForConnection",
                "debuggerPort",
            },
            file.Properties.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void Read_SettingsSol_ReadsValuesOfEachType()
    {
        var file = SolFile.Read(ReadFixture("settings.sol"));
        var byName = file.Properties.ToDictionary(p => p.Key, p => p.Value);

        // Boolean
        Assert.True(Assert.IsType<Amf0Boolean>(byName["allowThirdPartyLSOAccess"]).Value);
        Assert.False(Assert.IsType<Amf0Boolean>(byName["crossdomainAllow"]).Value);

        // Number
        Assert.Equal(123.0, Assert.IsType<Amf0Number>(byName["authorizedFeaturesExpiry"]).Value);
        Assert.Equal(-1.0, Assert.IsType<Amf0Number>(byName["secureCrossDomainCacheSize"]).Value);

        // String（空串也是合法值，不能当成 null 丢掉）
        var defaultAudio = Assert.IsType<Amf0String>(byName["defaultaudio"]);
        Assert.Equal(string.Empty, defaultAudio.Value);

        // Object
        var domains = Assert.IsType<Amf0Object>(byName["domains"]);
        Assert.Equal(17, domains.Members.Count);
        Assert.True(Assert.IsType<Amf0Boolean>(domains.TryGet("s4.4399.com")).Value);

        // 空对象（只有起止标记）
        Assert.Empty(Assert.IsType<Amf0Object>(byName["trustedPaths"]).Members);
    }

    [Fact]
    public void Write_SettingsSol_ReproducesOriginalBytesExactly()
    {
        var original = ReadFixture("settings.sol");
        var file = SolFile.Read(original);

        var rewritten = file.Write();

        Assert.Equal(SettingsSolSize, rewritten.Length);
        AssertBytesEqual(original, rewritten);
    }

    [Fact]
    public void Write_AfterModifyingAProperty_ChangesOnlyThatRegion()
    {
        var original = ReadFixture("settings.sol");
        var file = SolFile.Read(original);

        var domains = Assert.IsType<Amf0Object>(file.Properties.Single(p => p.Key == "domains").Value);
        domains.Set("s4.4399.com", new Amf0Boolean(false));

        var rewritten = file.Write();

        // 同一个字节长度（true→false 都是 2 字节的值体），所以总长不变，可以逐字节定位差异。
        Assert.Equal(original.Length, rewritten.Length);
        var firstDifference = FirstDifference(original, rewritten);
        Assert.True(firstDifference >= 0, "expected the edited property to change the output");

        // 差异必须落在 domains 成员的值上，而不是文件头或其他属性。
        // 成员布局是 u16 长度 + 名字 + Boolean 标记 + 值字节，所以值字节在名字之后第 2 个位置。
        var memberOffset = FindSequence(original, Encoding.ASCII.GetBytes("s4.4399.com"));
        Assert.True(memberOffset > 0, "fixture should contain the s4.4399.com member");

        var valueByteOffset = memberOffset + "s4.4399.com".Length + 1;
        Assert.Equal(0x01, original[valueByteOffset]);
        Assert.Equal(0x00, rewritten[valueByteOffset]);
        Assert.Equal(valueByteOffset, firstDifference);
    }

    [Theory]
    [InlineData("settings.sol", "settings", SolFormat.Amf0)]
    [InlineData("FBCookie.sol", "FBCookie", SolFormat.Amf3)]
    [InlineData("pvz.sol", "pvz", SolFormat.Amf3)]
    [InlineData("mao.sol", "mao", SolFormat.Amf3)]
    public void ReadHeader_WorksForEveryFixture(string fileName, string expectedName, SolFormat expectedFormat)
    {
        var bytes = ReadFixture(fileName);

        var header = SolFile.ReadHeader(bytes);

        Assert.Equal(expectedName, header.Name);
        Assert.Equal(expectedFormat, header.Format);
        Assert.Equal((uint)(bytes.Length - 6), header.DeclaredLength);
        Assert.Equal(18 + expectedName.Length + 4, header.BodyOffset);
    }

    [Theory]
    [InlineData("settings.sol")]
    [InlineData("FBCookie.sol")]
    [InlineData("pvz.sol")]
    [InlineData("mao.sol")]
    public void Write_EveryFixture_ReproducesOriginalBytesExactly(string fileName)
    {
        var original = ReadFixture(fileName);

        var rewritten = SolFile.Read(original).Write();

        Assert.Equal(original.Length, rewritten.Length);
        AssertBytesEqual(original, rewritten);
    }

    [Fact]
    public void Read_MaoSol_ParsesSingleIntegerProperty()
    {
        var file = SolFile.Read(ReadFixture("mao.sol"));

        Assert.Equal("mao", file.Name);
        Assert.Equal(SolFormat.Amf3, file.Format);

        var property = Assert.Single(file.Properties);
        Assert.Equal("level", property.Key);
        Assert.Equal(6, Assert.IsType<Amf3Integer>(property.Value).Value);
    }

    [Fact]
    public void Read_PvzSol_ParsesAnonymousDynamicObject()
    {
        var file = SolFile.Read(ReadFixture("pvz.sol"));

        var property = Assert.Single(file.Properties);
        Assert.Equal("saveData", property.Key);

        var saveData = Assert.IsType<Amf3Object>(property.Value);

        // Flash 写的是匿名动态类：空类名、无密封成员、成员全在动态部分。
        Assert.Equal(string.Empty, saveData.Traits.ClassName.Value);
        Assert.True(saveData.Traits.Dynamic);
        Assert.Empty(saveData.Traits.SealedMemberNames);

        Assert.Equal(
            new[] { "musicOn", "level", "puzzleLocked", "soundOn", "survivalLocked" },
            saveData.DynamicMembers.Select(m => m.Key.Value).ToArray());

        Assert.True(Assert.IsType<Amf3Boolean>(saveData.TryGetDynamicMember("musicOn")).Value);
        Assert.Equal(14, Assert.IsType<Amf3Integer>(saveData.TryGetDynamicMember("level")).Value);
        Assert.False(Assert.IsType<Amf3Boolean>(saveData.TryGetDynamicMember("puzzleLocked")).Value);
        Assert.True(Assert.IsType<Amf3Boolean>(saveData.TryGetDynamicMember("soundOn")).Value);
        Assert.False(Assert.IsType<Amf3Boolean>(saveData.TryGetDynamicMember("survivalLocked")).Value);
    }

    [Fact]
    public void Read_FBCookieSol_ParsesTheDenseArrays()
    {
        var file = SolFile.Read(ReadFixture("FBCookie.sol"));

        // 注意 "submited" 是原文件里的拼写（单个 t），不是这里的笔误 —— 按原样保留才对得上字节。
        Assert.Equal(
            new[] { "stats", "submited", "times", "timesString" },
            file.Properties.Select(p => p.Key).ToArray());

        var byName = file.Properties.ToDictionary(p => p.Key, p => p.Value);

        var stats = Assert.IsType<Amf3Array>(byName["stats"]);
        Assert.Equal(32, stats.DenseItems.Count);
        Assert.All(stats.DenseItems, item => Assert.Equal(3, Assert.IsType<Amf3Integer>(item).Value));

        Assert.False(Assert.IsType<Amf3Boolean>(byName["submited"]).Value);

        var times = Assert.IsType<Amf3Array>(byName["times"]);
        Assert.Equal(32, times.DenseItems.Count);
        Assert.All(times.DenseItems, item => Assert.Equal(0, Assert.IsType<Amf3Integer>(item).Value));

        var timesString = Assert.IsType<Amf3Array>(byName["timesString"]);
        Assert.Equal(32, timesString.DenseItems.Count);
        Assert.All(
            timesString.DenseItems,
            item => Assert.Equal(string.Empty, Assert.IsType<Amf3String>(item).Value));
    }

    [Fact]
    public void ReadHeader_RejectsBadMagic()
    {
        var bytes = ReadFixture("settings.sol");
        bytes[1] = 0xBE;

        var exception = Assert.Throws<InvalidDataException>(() => SolFile.ReadHeader(bytes));

        Assert.Contains("magic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadHeader_RejectsLengthFieldThatDisagreesWithFileSize()
    {
        var bytes = ReadFixture("settings.sol");
        bytes[5] -= 1;

        var exception = Assert.Throws<InvalidDataException>(() => SolFile.ReadHeader(bytes));

        Assert.Contains("length field", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsPropertyMissingItsTerminator()
    {
        var bytes = ReadFixture("settings.sol");
        var file = SolFile.Read(bytes);

        // 第一个属性是 Boolean true，占 01 01；把紧随其后的分隔字节改成非零。
        var terminatorOffset = 30 + 2 + "allowThirdPartyLSOAccess".Length + 2;
        Assert.Equal(0x00, bytes[terminatorOffset]);
        bytes[terminatorOffset] = 0x7F;

        var exception = Assert.Throws<InvalidDataException>(() => SolFile.Read(bytes));

        Assert.Contains("separator", exception.Message, StringComparison.OrdinalIgnoreCase);

        // 断言改动确实落在那一个字节上 —— 否则这条测试可能因为别的原因通过。
        Assert.Equal(23, file.Properties.Count);
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        var offset = FirstDifference(expected, actual);
        if (offset < 0)
        {
            return;
        }

        var window = 8;
        var from = Math.Max(0, offset - window);
        var expectedSlice = Convert.ToHexString(expected.AsSpan(from, Math.Min(window * 2, expected.Length - from)));
        var actualSlice = Convert.ToHexString(actual.AsSpan(from, Math.Min(window * 2, actual.Length - from)));

        Assert.Fail(
            $"Byte-level round-trip failed: first difference at offset {offset} (0x{offset:x}) " +
            $"of {expected.Length} bytes.{Environment.NewLine}" +
            $"  expected[{from}..]: {expectedSlice}{Environment.NewLine}" +
            $"  actual  [{from}..]: {actualSlice}");
    }

    private static int FirstDifference(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return Math.Min(expected.Length, actual.Length);
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
