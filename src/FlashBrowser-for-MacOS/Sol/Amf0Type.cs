namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF0 类型标记（Action Message Format 0，Adobe，2007）。
///
/// 标记值取自 AMF0 规范。其中三个不是可独立存在的值：
/// <see cref="ObjectEnd"/> 只作为对象 / 关联数组的结束标记出现（<c>00 00 09</c>）；
/// <see cref="MovieClip"/>、<see cref="Unsupported"/>、<see cref="Recordset"/> 是规范里
/// 保留或标记为「不支持」的槽位；<see cref="AvmPlus"/> 表示「后续字节按 AMF3 解析」，
/// 它不是 AMF0 值，本项目在 AMF0 读取器里对它显式报错而不是猜测语义。
/// </summary>
public enum Amf0Type : byte
{
    Number = 0x00,
    Boolean = 0x01,
    String = 0x02,
    Object = 0x03,
    MovieClip = 0x04,
    Null = 0x05,
    Undefined = 0x06,
    Reference = 0x07,
    EcmaArray = 0x08,
    ObjectEnd = 0x09,
    StrictArray = 0x0A,
    Date = 0x0B,
    LongString = 0x0C,
    Unsupported = 0x0D,
    Recordset = 0x0E,
    XmlDocument = 0x0F,
    TypedObject = 0x10,
    AvmPlus = 0x11,
}
