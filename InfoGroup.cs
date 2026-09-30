namespace SystemInfoGUI;

/// <summary>
/// 系统信息的显示分组。
/// </summary>
/// <remarks>
/// 本枚举是 <see cref="SystemInfoCollector"/> 与 <see cref="SystemInfoForm"/> 之间的显式契约：
/// 采集侧在产出每一行时必须声明其分组，展示侧据此归组渲染，不再依赖属性名字符串前缀匹配。
/// <para>
/// <strong>枚举声明顺序即界面分组的展示顺序</strong>，调整展示次序时应调整此处的声明次序，
/// 而不是在展示侧硬编码顺序。
/// </para>
/// </remarks>
public enum InfoGroup
{
    /// <summary>不属于任何专题分组的信息，直接罗列、不显示分组标题。</summary>
    Other,

    /// <summary>电源设置：屏幕关闭超时、睡眠超时、电源方案。</summary>
    Power,

    /// <summary>防火墙状态：各网络位置的防火墙启用情况。</summary>
    Firewall,

    /// <summary>IP 配置：各网卡的 IPv4 / IPv6 地址与网关。</summary>
    IpConfig,

    /// <summary>网络适配器：网卡型号与数量。</summary>
    NetworkAdapter,

    /// <summary>磁盘分区：本地磁盘、可移动磁盘、网络驱动器、光驱。</summary>
    Disk
}

/// <summary>
/// <see cref="InfoGroup"/> 的展示文案扩展。
/// </summary>
public static class InfoGroupExtensions
{
    /// <summary>
    /// 获取分组在界面上的标题文字。<see cref="InfoGroup.Other"/> 返回空串，表示不显示分组标题。
    /// </summary>
    public static string ToTitle(this InfoGroup group) => group switch
    {
        InfoGroup.Power => "电源设置",
        InfoGroup.Firewall => "防火墙状态",
        InfoGroup.IpConfig => "IP配置",
        InfoGroup.NetworkAdapter => "网络适配器",
        InfoGroup.Disk => "磁盘分区",
        _ => string.Empty
    };
}
