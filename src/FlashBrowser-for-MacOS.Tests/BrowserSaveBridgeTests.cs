using System.Text.Json;
using FlashBrowserForMacOS.Sol;
using Xunit;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// 存档桥脚本的逃逸契约：键名/存档名会以 JS 字符串字面量嵌入页面脚本，
/// 引号、反斜杠等字符必须经 JSON 逃逸（JSON 字符串字面量是 JS 的合法子集），
/// 否则构造出的脚本会被注入或语法错误。
/// </summary>
public class BrowserSaveBridgeTests
{
    [Theory]
    [InlineData("simple-key")]
    [InlineData("host/path with space/main.swf/档名")]
    [InlineData("quote\"and\\slash/x")]
    [InlineData("line\nbreak")]
    public void Fetch_and_put_scripts_embed_the_key_json_escaped(string key)
    {
        var expected = JsonSerializer.Serialize(key);

        Assert.Contains(expected, BrowserSaveBridge.FetchSaveScript(key));
        Assert.Contains(expected, BrowserSaveBridge.PutSaveScript(key, "QUJD"));
    }

    [Fact]
    public void Derive_key_script_embeds_the_sol_name_json_escaped()
    {
        var name = "pvz\"x";
        Assert.Contains(JsonSerializer.Serialize(name), BrowserSaveBridge.DeriveKeyScript(name));
    }

    [Fact]
    public void List_saves_script_is_a_returning_expression()
    {
        // EvaluateJavaScript 的脚本必须自带 return（上游引擎把脚本放进函数体）。
        Assert.Contains("return", BrowserSaveBridge.ListSavesScript);
        Assert.Contains("TCSO", BrowserSaveBridge.ListSavesScript);
    }
}
