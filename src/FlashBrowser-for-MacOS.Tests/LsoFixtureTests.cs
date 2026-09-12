using System.Buffers.Binary;
using System.Text;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// 上游 LSO fixture 的表征测试（characterization tests）。
///
/// 目的不是测业务代码，而是把 README §TODO 里 P1 的前提**锁死**：上游 fixture 共 4 个，
/// 其中 AMF0 恰好 1 个（<c>settings.sol</c>）、AMF3 3 个。若 fixture 被替换或增删，
/// 这里会先失败，而不是等到 P1 解析器的断言莫名变红。
///
/// LSO 头布局（依据 docs.rs/flash-lso <c>read.rs</c> + SANS「Super Timeline」）：
/// <code>
///   00 BF | u32 payload length | "TCSO" 00 04 00 00 00 00
///   | u16 name length | name | 3 B padding | 1 B AMF version (0x00=AMF0 / 0x03=AMF3)
/// </code>
/// </summary>
public class LsoFixtureTests
{
    private const byte Amf0 = 0x00;
    private const byte Amf3 = 0x03;

    private static string TestDataDir => Path.Combine(AppContext.BaseDirectory, "TestData");

    private static readonly string[] ExpectedFixtures =
    {
        "FBCookie.sol",
        "mao.sol",
        "pvz.sol",
        "settings.sol",
    };

    [Fact]
    public void TestData_HoldsExactlyTheFourExpectedFixtures()
    {
        var actual = Directory.GetFiles(TestDataDir, "*.sol")
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedFixtures, actual);
    }

    [Theory]
    [InlineData("settings.sol", "settings", Amf0, 845)]
    [InlineData("FBCookie.sol", "FBCookie", Amf3, 269)]
    [InlineData("pvz.sol", "pvz", Amf3, 95)]
    [InlineData("mao.sol", "mao", Amf3, 34)]
    public void Fixture_HasWellFormedLsoHeader(string fileName, string expectedName, byte expectedVersion, int expectedSize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestDataDir, fileName));

        var header = ParseHeader(bytes);

        Assert.Equal(expectedSize, bytes.Length);
        Assert.Equal(expectedName, header.Name);
        Assert.Equal(expectedVersion, header.Version);
        Assert.Equal((uint)(bytes.Length - 6), header.DeclaredLength);
    }

    [Fact]
    public void FixtureSet_IsOneAmf0AndThreeAmf3()
    {
        var versions = ExpectedFixtures
            .Select(f => ParseHeader(File.ReadAllBytes(Path.Combine(TestDataDir, f))).Version)
            .ToArray();

        Assert.Equal(1, versions.Count(v => v == Amf0));
        Assert.Equal(3, versions.Count(v => v == Amf3));
    }

    /// <summary>解析 LSO 头，并用断言守住格式前提（魔数 / TCSO 块 / 3 B padding / 版本字节）。</summary>
    private static LsoHeader ParseHeader(byte[] bytes)
    {
        Assert.True(bytes.Length >= 24, "file is too short to hold an LSO header");

        Assert.Equal<byte>(new byte[] { 0x00, 0xBF }, bytes[..2]);
        Assert.Equal("TCSO", Encoding.ASCII.GetString(bytes, 6, 4));
        Assert.Equal<byte>(new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 }, bytes[10..16]);

        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2, 4));
        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16, 2));
        Assert.True(nameLength > 0, "LSO name is empty");

        var name = Encoding.ASCII.GetString(bytes, 18, nameLength);
        var versionOffset = 18 + nameLength;

        Assert.Equal<byte>(new byte[] { 0x00, 0x00, 0x00 }, bytes[versionOffset..(versionOffset + 3)]);

        var version = bytes[versionOffset + 3];
        Assert.True(version is Amf0 or Amf3, $"unexpected AMF version 0x{version:x2}");

        return new LsoHeader(name, version, declaredLength);
    }

    private sealed record LsoHeader(string Name, byte Version, uint DeclaredLength);
}
