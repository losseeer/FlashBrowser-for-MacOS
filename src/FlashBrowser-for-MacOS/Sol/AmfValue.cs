namespace FlashBrowserForMacOS.Sol;

/// <summary>
/// 一个 AMF 值的公共基类 —— 存在的唯一理由是让 <see cref="Models.SolFile"/> 的属性表
/// 能同时装 AMF0 与 AMF3 的值。
///
/// <para>这里刻意**不**放任何共用成员。两种格式的类型体系没有有意义的交集：AMF0 只有双精度
/// <c>Number</c>，AMF3 另有 29 位 <c>Integer</c>；AMF3 还有三张引用表，AMF0 的引用表用法也不同。
/// 硬抽一个「统一值接口」只会让人误以为可以跨格式取值 —— 实际上一个 AMF0 值放进 AMF3 文件里
/// 就是错的，<see cref="Models.SolFile.Write"/> 会显式拒绝。</para>
/// </summary>
public abstract class AmfValue
{
}
