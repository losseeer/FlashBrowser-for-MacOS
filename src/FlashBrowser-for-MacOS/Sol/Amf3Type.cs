namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// AMF3 类型标记（Action Message Format 3，Adobe，2008）。
///
/// 与 AMF0 不同，AMF3 里「标记即全部」的情形更多：<see cref="False"/> 与 <see cref="True"/>
/// 各自是一个完整的值，标记之后没有 payload；<see cref="Undefined"/>、<see cref="Null"/> 同理。
/// 而 <see cref="Integer"/> 与 <see cref="Double"/> 是两种不同的字节形态，数值范围却重叠
/// （整数用小得多的 U29，等价的双精度数也可以走 Double），所以必须分开建模，
/// 归并成一个「数字」会在写回时改变字节。
///
/// AMF3 还有三张**引用表**（字符串 / 对象 / traits），同一个值第二次出现时可能只写一个下标，
/// 因此每种可被引用的值在 <see cref="Amf3Value"/> 里都保留了自己的引用下标。
/// </summary>
public enum Amf3Type : byte
{
    /// <summary>无 payload，对应 ActionScript 的 <c>undefined</c>。</summary>
    Undefined = 0x00,

    /// <summary>无 payload，对应 ActionScript 的 <c>null</c>。</summary>
    Null = 0x01,

    /// <summary>无 payload，布尔假。与 <see cref="True"/> 是两个独立标记。</summary>
    False = 0x02,

    /// <summary>无 payload，布尔真。</summary>
    True = 0x03,

    /// <summary>29 位有符号整数（U29，非引用形态；与 AMF0 的双精度不同）。</summary>
    Integer = 0x04,

    /// <summary>8 字节大端 IEEE-754 双精度。</summary>
    Double = 0x05,

    /// <summary>U29S 字符串，可写成字符串引用表的下标。</summary>
    String = 0x06,

    /// <summary>XMLDocument，形态与 <see cref="String"/> 相同（U29S），但参与对象引用表。</summary>
    XmlDocument = 0x07,

    /// <summary>8 字节大端双精度毫秒（无 AMF0 那样的时区字段）。</summary>
    Date = 0x08,

    /// <summary>稠密部分 + 关联部分，两段个数各用一个 U29。</summary>
    Array = 0x09,

    /// <summary>对象。标记之后紧跟一个**对象与 traits 共用的 U29**，见 <see cref="Amf3Reader"/>。</summary>
    Object = 0x0A,

    /// <summary>E4X XML，形态与 <see cref="String"/> 相同（U29S）。</summary>
    Xml = 0x0B,

    /// <summary>U29 字节长度 + 原始字节，参与对象引用表。</summary>
    ByteArray = 0x0C,

    /// <summary>定长/变长 <c>Vector.&lt;int&gt;</c>；元素是 4 字节大端原始值，不是 AMF3 值。</summary>
    VectorInt = 0x0D,

    /// <summary><c>Vector.&lt;uint&gt;</c>；元素是 4 字节大端原始值。</summary>
    VectorUint = 0x0E,

    /// <summary><c>Vector.&lt;Number&gt;</c>；元素是 8 字节大端原始值。</summary>
    VectorDouble = 0x0F,

    /// <summary><c>Vector.&lt;Object&gt;</c>；元素是 AMF3 值，且多一个元素类型名字段。</summary>
    VectorObject = 0x10,

    /// <summary>Dictionary（Flash Player 10+）。键与值都是 AMF3 值。</summary>
    Dictionary = 0x11,
}
