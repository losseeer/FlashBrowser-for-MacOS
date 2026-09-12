using FlashBrowserForMacOS.Models;
using FlashBrowserForMacOS.Sol;
using Xunit;

namespace FlashBrowserForMacOS.Tests;

/// <summary>
/// P1.4 行模型的行为约束：编辑必须原地写回、解析失败必须显式报错且不动原值、
/// 未经编辑的文件经过行模型仍字节级 round-trip。
/// </summary>
public class SolPropertyRowTests
{
    [Fact]
    public void FromFile_covers_every_property_in_order()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        sol.Add("num", new Amf0Number(1.5));
        sol.Add("flag", new Amf0Boolean(true));
        sol.Add("txt", new Amf0String("hi"));

        var rows = SolPropertyRow.FromFile(sol);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["num", "flag", "txt"], rows.Select(r => r.Name));
        Assert.Equal("Number", rows[0].TypeName);
        Assert.Equal("Boolean", rows[1].TypeName);
        Assert.Equal("String", rows[2].TypeName);
    }

    [Fact]
    public void Number_edit_is_applied_in_place()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        var number = new Amf0Number(1.5);
        sol.Add("num", number);

        var row = SolPropertyRow.FromFile(sol)[0];
        row.TextValue = "42.25";

        Assert.True(row.TryApply(out var error));
        Assert.Null(error);
        Assert.Equal(42.25, number.Value);
        Assert.Same(number, sol.Properties[0].Value);
    }

    [Fact]
    public void Invalid_number_reports_error_and_keeps_value()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        var number = new Amf0Number(1.5);
        sol.Add("num", number);

        var row = SolPropertyRow.FromFile(sol)[0];
        row.TextValue = "abc";

        Assert.False(row.TryApply(out var error));
        Assert.NotNull(error);
        Assert.Contains("num", error);
        Assert.Equal(1.5, number.Value);
    }

    [Fact]
    public void Boolean_edit_round_trips_through_checkbox_value()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        var flag = new Amf0Boolean(false);
        sol.Add("flag", flag);

        var row = SolPropertyRow.FromFile(sol)[0];
        Assert.False(row.BoolValue);
        Assert.True(row.IsBoolean);
        Assert.False(row.IsTextBox);

        row.BoolValue = true;
        Assert.True(row.TryApply(out _));
        Assert.True(flag.Value);
    }

    [Fact]
    public void String_edit_keeps_object_identity()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        var text = new Amf0String("hi");
        sol.Add("txt", text);

        var row = SolPropertyRow.FromFile(sol)[0];
        row.TextValue = "changed";

        Assert.True(row.TryApply(out _));
        Assert.Equal("changed", text.Value);
        Assert.Same(text, sol.Properties[0].Value);
    }

    [Fact]
    public void Amf3_integer_rejects_out_of_range_values()
    {
        var sol = new SolFile("t", SolFormat.Amf3);
        var integer = new Amf3Integer(6);
        sol.Add("level", integer);

        var row = SolPropertyRow.FromFile(sol)[0];

        row.TextValue = (Amf3Integer.MaxValue + 1L).ToString();
        Assert.False(row.TryApply(out var tooBig));
        Assert.Contains("范围", tooBig);

        row.TextValue = (Amf3Integer.MinValue - 1L).ToString();
        Assert.False(row.TryApply(out var tooSmall));

        row.TextValue = "300";
        Assert.True(row.TryApply(out _));
        Assert.Equal(300, integer.Value);
    }

    [Fact]
    public void Amf3_string_with_reference_index_edits_content_and_keeps_reference()
    {
        var sol = new SolFile("t", SolFormat.Amf3);
        var text = new Amf3String("shared") { ReferenceIndex = 0 };
        sol.Add("s", text);

        var row = SolPropertyRow.FromFile(sol)[0];
        Assert.Equal("String @str(0)", row.TypeName);

        row.TextValue = "edited";
        Assert.True(row.TryApply(out _));

        Assert.Equal("edited", text.Value);
        Assert.Equal(0, text.ReferenceIndex);
        Assert.Same(text, sol.Properties[0].Value);
    }

    [Fact]
    public void Unedited_rows_leave_byte_level_round_trip_untouched()
    {
        foreach (var fixture in new[] { "settings.sol", "FBCookie.sol", "pvz.sol", "mao.sol" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", fixture);
            var bytes = File.ReadAllBytes(path);
            var sol = SolFile.Read(bytes);
            _ = SolPropertyRow.FromFile(sol);

            Assert.True(bytes.AsSpan().SequenceEqual(sol.Write()), $"{fixture} must round-trip byte-for-byte.");
        }
    }

    [Fact]
    public void Edited_file_writes_the_new_value_and_still_reparses()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        sol.Add("num", new Amf0Number(1.5));
        sol.Add("txt", new Amf0String("hi"));

        var rows = SolPropertyRow.FromFile(sol);
        rows[0].TextValue = "42.25";
        rows[0].TryApply(out _);
        rows[1].TextValue = "changed";
        rows[1].TryApply(out _);

        var reparsed = SolFile.Read(sol.Write());

        Assert.Equal(42.25, Assert.IsType<Amf0Number>(reparsed.Properties[0].Value).Value);
        Assert.Equal("changed", Assert.IsType<Amf0String>(reparsed.Properties[1].Value).Value);
    }

    [Fact]
    public void Complex_values_are_read_only_with_display_text()
    {
        var sol = new SolFile("t", SolFormat.Amf3);
        var obj = new Amf3Object(new Amf3Traits(new Amf3String(""), externalizable: false, dynamic: true));
        sol.Add("saveData", obj);

        var row = SolPropertyRow.FromFile(sol)[0];

        Assert.True(row.IsDisplayOnly);
        Assert.False(row.IsTextBox);
        Assert.False(row.IsBoolean);
        Assert.NotEmpty(row.DisplayValue);
    }

    [Fact]
    public void IsEdited_tracks_only_actual_changes()
    {
        var sol = new SolFile("t", SolFormat.Amf0);
        sol.Add("num", new Amf0Number(1.5));

        var row = SolPropertyRow.FromFile(sol)[0];
        Assert.False(row.IsEdited);

        row.TextValue = "2";
        Assert.True(row.IsEdited);

        // 回到原值应不再算编辑过（避免无意义的保存提示）。
        row.TextValue = "1.5";
        Assert.False(row.IsEdited);
    }
}
