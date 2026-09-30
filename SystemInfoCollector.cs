using System.Data;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SystemInfoGUI;

[SupportedOSPlatform("windows")]
public static class SystemInfoCollector
{
    // 失败文案写入 DataTable 后由 DataGridView 单行展示，需限制长度，避免异常信息撑爆单元格。
    private const int MaxFailValueLength = 80;

    private static int SafeConvertInt(object? value) => value == null ? 0 : Convert.ToInt32(value);

    private static string FormatPowerTimeout(object? value)
    {
        if (value == null)
            return "读取失败";

        int seconds = SafeConvertInt(value);
        return seconds switch
        {
            0 => "从不",
            < 60 => $"{seconds} 秒",
            _ => $"{seconds / 60} 分钟"
        };
    }

    private static uint? SafeGetUInt(object? value)
    {
        if (value == null) return null;
        string str = value.ToString() ?? string.Empty;
        if (uint.TryParse(str, out uint result))
            return result;
        return null;
    }

    /// <summary>
    /// 生成面向用户的失败文案。仅附加异常类型名而不拼接 Message，
    /// 既保留现场可诊断信息，又避免内部细节/换行污染 DataGridView 的单行展示。
    /// </summary>
    private static string Fail(string reason, Exception? ex = null)
        => TruncateForGrid(ex == null ? reason : $"{reason}（{ex.GetType().Name}）");

    /// <summary>
    /// 将文本压成单行并按最大长度截断，保证 DataGridView 单元格不会因异常信息变得过长。
    /// </summary>
    private static string TruncateForGrid(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= MaxFailValueLength
            ? singleLine
            : singleLine.Substring(0, MaxFailValueLength) + "...";
    }

    /// <summary>
    /// 按协议族筛选并拼接地址列表。WMI 的 IPAddress 同时包含 IPv4 与 IPv6，
    /// 依据是否包含 ':' 判定 IPv6；无匹配项时返回“无”。
    /// </summary>
    private static string JoinAddresses(string[]? addresses, bool ipv6)
    {
        if (addresses == null || addresses.Length == 0)
            return "无";

        var matched = new List<string>();
        foreach (string? address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
                continue;

            bool isIPv6 = address.Contains(':');
            if (isIPv6 == ipv6)
                matched.Add(address);
        }

        return matched.Count == 0 ? "无" : string.Join(", ", matched);
    }

    /// <summary>
    /// 读取卷的 BitLocker 保护状态原始值。
    /// 优先走 GetProtectionStatus 方法的输出参数，方法不可用时回退到同名属性；
    /// 两者都取不到时返回 uint.MaxValue 表示无法判定（由调用方映射为“检测失败”）。
    /// </summary>
    private static uint ReadProtectionStatus(ManagementObject volume)
    {
        // GetProtectionStatus 的返回值是 HRESULT（0 = 调用成功），
        // 真实加密状态位于输出参数 ProtectionStatus 中，不可直接 Convert 返回值。
        try
        {
            using ManagementBaseObject? outParams = volume.InvokeMethod("GetProtectionStatus", null, null);
            if (outParams?["ProtectionStatus"] != null)
                return Convert.ToUInt32(outParams["ProtectionStatus"]);
        }
        catch { /* 方法调用失败，回退到属性读取 */ }

        try
        {
            object? statusObj = volume["ProtectionStatus"];
            if (statusObj != null)
                return Convert.ToUInt32(statusObj);
        }
        catch { /* 保持未知 */ }

        return uint.MaxValue;
    }

    /// <summary>
    /// 将 Win32_EncryptableVolume 的 ProtectionStatus 映射为面向用户的文案。
    /// </summary>
    private static string ConvertProtectionStatus(uint protectionStatus) => protectionStatus switch
    {
        0 => "未加密",
        1 => "已加密",
        2 => "状态未知",
        uint.MaxValue => "检测失败",
        _ => $"状态 {protectionStatus}"
    };

    /// <summary>
    /// 查询指定盘符卷的 BitLocker 状态。
    /// Win32_EncryptableVolume 位于 root\CIMV2\Security\MicrosoftVolumeEncryption，
    /// 默认 root\cimv2 查询必然抛出 Invalid class，必须显式指定作用域。
    /// </summary>
    /// <remarks>
    /// 对“查不到”与“没加密”必须严格区分，否则审计工具会产生危险的假阴性。因此返回三种文案：
    /// <list type="bullet">
    /// <item>命名空间连接失败或被拒（非提升权限）/ 类不存在 / 连接成功后枚举或读状态失败 → 无权查询（需管理员权限）</item>
    /// <item>命名空间可用但未匹配到该盘符的卷 → 未检测到 BitLocker</item>
    /// <item>匹配到卷并读到状态 → 按 ProtectionStatus 映射</item>
    /// </list>
    /// </remarks>
    private static string DetectBitLockerStatus(string letter)
    {
        try
        {
            var scope = new ManagementScope(@"root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();

            using var bmSearcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='{letter}'"));

            // null 表示命名空间可用但未匹配到该盘符的卷；只有整个查询（含 Get()）未抛异常才采信。
            string? status = null;
            foreach (ManagementObject bm in bmSearcher.Get())
            {
                using (bm)
                {
                    status = ConvertProtectionStatus(ReadProtectionStatus(bm));
                }
                break;
            }

            return status ?? "未检测到 BitLocker";
        }
        catch
        {
            // 非提升权限下连接该命名空间会抛 ManagementException（拒绝访问）。此处绝不能回退成
            // “未加密”，否则无权查询会被静默伪装成“该盘未加密”。
            return "无权查询（需管理员权限）";
        }
    }

    /// <summary>
    /// 将 Win32_LogicalDisk 的 Size 属性格式化为 GB 文本。网络驱动器等场景 Size 可能为 null，
    /// 此处统一容错为 N/A。
    /// </summary>
    private static string FormatDiskSize(object? value)
    {
        ulong size = 0;
        ulong.TryParse(value?.ToString(), out size);
        return size > 0 ? $"{size / (1024.0 * 1024 * 1024):F2} GB" : "N/A";
    }

    /// <summary>
    /// 判断 Win32_LogicalDisk 的 Size 是否为有效正数。
    /// 无介质的读卡器空槽 Size 为 null 或 0，需要据此过滤，避免多合一读卡器刷屏。
    /// </summary>
    private static bool HasDiskSize(object? value)
    {
        ulong size = 0;
        ulong.TryParse(value?.ToString(), out size);
        return size > 0;
    }

    /// <summary>
    /// 向结果表追加一行，并显式声明该行所属分组。
    /// </summary>
    /// <remarks>
    /// 分组由调用方以 <see cref="InfoGroup"/> 显式传入，展示侧据此归组。
    /// 这样属性名不再承担分类职责，重命名属性不会导致某行意外掉出分组。
    /// </remarks>
    private static void AddRow(DataTable table, InfoGroup group, string prop, string value)
        => table.Rows.Add(prop, value, group);

    private static string? GetActivePowerSchemeGuid()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
        {
            if (key != null)
            {
                object? activeValue = key.GetValue("Active");
                if (activeValue != null)
                {
                    string? guid = activeValue.ToString();
                    if (!string.IsNullOrEmpty(guid))
                        return guid;
                }
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = "/getactivescheme",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
            return null;

        // 带超时等待退出，超时则强制终止，避免子进程卡死导致界面永久阻塞。
        // powercfg 输出远小于管道缓冲区（约 4 KB），先等待退出再读取不会产生管道死锁。
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出或无权限 */ }
            return null;
        }

        string output = process.StandardOutput.ReadToEnd();
        var match = Regex.Match(output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// 采集系统信息。
    /// </summary>
    /// <returns>
    /// 三列 <see cref="DataTable"/>：<c>属性</c>(string)、<c>值</c>(string)、<c>分组</c>(<see cref="InfoGroup"/>)。
    /// 前两列的索引与语义保持历史兼容；第三列是分组契约的显式载体，由展示侧读取，不参与界面呈现。
    /// </returns>
    public static DataTable GetInfoTable()
    {
        var table = new DataTable();
        table.Columns.Add("属性", typeof(string));
        table.Columns.Add("值", typeof(string));
        table.Columns.Add("分组", typeof(InfoGroup));

        // 1. 序列号
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                string? serial = obj["SerialNumber"]?.ToString();
                AddRow(table, InfoGroup.Other, "本机序列号", serial ?? "N/A");
                break;
            }
        }
        catch (Exception ex) { AddRow(table, InfoGroup.Other, "本机序列号", Fail("读取失败", ex)); }

        // 2. 用户名
        AddRow(table, InfoGroup.Other, "本机用户名", Environment.UserName ?? "未知");

        // 3. 电源超时
        string? activeSchemeGuid = GetActivePowerSchemeGuid();
        if (!string.IsNullOrEmpty(activeSchemeGuid))
        {
            string screenRegPath = $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeSchemeGuid}\7516b95f-f776-4464-8c53-06167f40cc99\3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
            using (var key = Registry.LocalMachine.OpenSubKey(screenRegPath))
            {
                AddRow(table, InfoGroup.Power, "屏幕关闭超时(AC)", FormatPowerTimeout(key?.GetValue("ACSettingIndex")));
                AddRow(table, InfoGroup.Power, "屏幕关闭超时(DC)", FormatPowerTimeout(key?.GetValue("DCSettingIndex")));
            }

            string sleepRegPath = $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeSchemeGuid}\238c9fa8-0aad-41ed-83f4-97be242c8f20\29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
            using (var key = Registry.LocalMachine.OpenSubKey(sleepRegPath))
            {
                AddRow(table, InfoGroup.Power, "睡眠超时(AC)", FormatPowerTimeout(key?.GetValue("ACSettingIndex")));
                AddRow(table, InfoGroup.Power, "睡眠超时(DC)", FormatPowerTimeout(key?.GetValue("DCSettingIndex")));
            }
        }
        else
        {
            // 活动电源方案 GUID 取不到时，超时两项无法定位注册表路径，仅输出方案行。
            AddRow(table, InfoGroup.Power, "电源方案", "无法获取活动 GUID");
        }

        // 4. 防火墙
        Type? fwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        if (fwType != null)
        {
            object? fwObj = null;
            string createError = "无法创建对象";
            try
            {
                fwObj = Activator.CreateInstance(fwType);
            }
            catch (Exception ex)
            {
                // COM 创建失败（组件未注册或服务不可用），保留异常类型便于现场排查
                createError = Fail(createError, ex);
            }

            if (fwObj != null)
            {
                dynamic fw = fwObj;
                var profiles = new Dictionary<int, string> { { 1, "域网络" }, { 2, "专用网络" }, { 4, "公用网络" } };
                foreach (var kvp in profiles)
                {
                    try
                    {
                        bool enabled = fw.FirewallEnabled(kvp.Key);
                        AddRow(table, InfoGroup.Firewall, $"防火墙 - {kvp.Value}", enabled ? "启用" : "禁用");
                    }
                    catch (Exception ex) { AddRow(table, InfoGroup.Firewall, $"防火墙 - {kvp.Value}", Fail("获取失败", ex)); }
                }
            }
            else
            {
                // 对象创建失败时无法逐档查询，只输出一行降级说明。
                AddRow(table, InfoGroup.Firewall, "防火墙", createError);
            }
        }
        else
        {
            AddRow(table, InfoGroup.Firewall, "防火墙", "COM 类型不可用");
        }

        // 5. 网口 IP 配置
        var indices = new List<uint>();
        using (var searcher = new ManagementObjectSearcher(
            "SELECT Index FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
        {
            foreach (ManagementObject ad in searcher.Get())
            {
                uint? idx = SafeGetUInt(ad["Index"]);
                if (idx.HasValue) indices.Add(idx.Value);
            }
        }
        if (indices.Count == 0)
        {
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Index FROM Win32_NetworkAdapter WHERE AdapterType='Ethernet 802.3'"))
            {
                foreach (ManagementObject ad in searcher.Get())
                {
                    uint? idx = SafeGetUInt(ad["Index"]);
                    if (idx.HasValue) indices.Add(idx.Value);
                }
            }
        }

        if (indices.Count > 0)
        {
            bool hasIp = false;
            foreach (uint idx in indices)
            {
                using var cfgSearcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE Index={idx} AND IPEnabled=True");
                foreach (ManagementObject cfg in cfgSearcher.Get())
                {
                    hasIp = true;
                    string desc = cfg["Description"]?.ToString() ?? "未知";
                    string[]? ips = cfg["IPAddress"] as string[];
                    string[]? gateways = cfg["DefaultIPGateway"] as string[];

                    // IPAddress 同时含 IPv4 与 IPv6，按是否含 ':' 分流，
                    // 全部输出而不是只取首个，避免多 IP 绑定场景丢数据。
                    //
                    // v4 与 v6 分行展示：网关同样按协议拆分并跟随对应行，避免 v4/v6 网关混在一行。
                    // 两行始终输出（无地址时填“无”），审计时需要明确看到“该网卡没有 IPv6”。
                    // 属性名保留网卡描述以便区分多网卡，分组由 InfoGroup.IpConfig 显式声明。
                    AddRow(table, InfoGroup.IpConfig, $"网卡 - {desc} (IPv4)",
                        $"IP: {JoinAddresses(ips, ipv6: false)} | 网关: {JoinAddresses(gateways, ipv6: false)}");
                    AddRow(table, InfoGroup.IpConfig, $"网卡 - {desc} (IPv6)",
                        $"IP: {JoinAddresses(ips, ipv6: true)} | 网关: {JoinAddresses(gateways, ipv6: true)}");
                }
            }
            if (!hasIp)
                AddRow(table, InfoGroup.IpConfig, "网口配置", "未找到有 IP 配置的物理网卡");
        }
        else
        {
            AddRow(table, InfoGroup.IpConfig, "网口配置", "未找到物理网卡");
        }

        // 6. 网卡型号及数量
        int count = 0;
        using var searcherN = new ManagementObjectSearcher(
            "SELECT Name, Description FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True");
        foreach (ManagementObject ad in searcherN.Get())
        {
            string name = ad["Name"]?.ToString() ?? "未知";
            string desc = ad["Description"]?.ToString() ?? "未知";
            AddRow(table, InfoGroup.NetworkAdapter, $"网卡型号 #{++count}", $"{name} - {desc}");
        }
        if (count == 0)
        {
            using var backup = new ManagementObjectSearcher(
                "SELECT Name, Description FROM Win32_NetworkAdapter WHERE AdapterType='Ethernet 802.3' AND NetEnabled=True");
            foreach (ManagementObject ad in backup.Get())
            {
                string name = ad["Name"]?.ToString() ?? "未知";
                string desc = ad["Description"]?.ToString() ?? "未知";
                AddRow(table, InfoGroup.NetworkAdapter, $"网卡型号 #{++count}", $"{name} - {desc}");
            }
        }
        if (count == 0)
            AddRow(table, InfoGroup.NetworkAdapter, "网卡数量", "未找到物理网卡");
        else
            AddRow(table, InfoGroup.NetworkAdapter, "物理网卡总数", count.ToString());

        // 7. 硬盘分区（WQL 不支持 IN，用 OR 展开：2 可移动 / 3 本地 / 4 网络 / 5 光驱）
        using var searcherD = new ManagementObjectSearcher(
            "SELECT * FROM Win32_LogicalDisk WHERE DriveType=2 OR DriveType=3 OR DriveType=4 OR DriveType=5");
        foreach (ManagementObject disk in searcherD.Get())
        {
            string letter = disk["DeviceID"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(letter)) continue;

            int driveType = SafeConvertInt(disk["DriveType"]);
            string sizeGB = FormatDiskSize(disk["Size"]);

            string mediaType = "未知";
            bool detectMediaType = false;
            bool detectBitLocker = false;

            switch (driveType)
            {
                case 5: // 光驱：无容量与加密概念
                    AddRow(table, InfoGroup.Disk, $"硬盘 {letter}", "光驱");
                    continue;

                case 4: // 网络驱动器：Size 常为 null，容错输出；不跑介质探测与 BitLocker 检测
                    AddRow(table, InfoGroup.Disk, $"硬盘 {letter}", $"大小: {sizeGB}, 类型: 网络驱动器");
                    continue;

                case 2: // 可移动磁盘（U 盘 / BitLocker To Go）：介质类型固定，但仍需 BitLocker 检测
                    // 无介质的读卡器空槽 Size 为 null 或 0，跳过以免多合一读卡器刷屏
                    if (!HasDiskSize(disk["Size"]))
                        continue;
                    mediaType = "可移动磁盘";
                    detectBitLocker = true;
                    break;

                case 3: // 本地磁盘：介质类型 + BitLocker 完整探测
                    detectMediaType = true;
                    detectBitLocker = true;
                    break;

                default: // WQL 已限定 2/3/4/5，其余值属防御性兜底，不得落入本地磁盘逻辑
                    continue;
            }

            // 默认值仅在 detectBitLocker 为 false 时保留（当前 WQL 过滤下不可达），
            // 真实状态一律由 DetectBitLockerStatus 给出，该方法的失败文案不会伪装成“未加密”。
            string bitlockerStatus = "不适用";

            // 检测介质类型（仅本地磁盘执行；U 盘的 ASSOCIATORS 关联查询无意义且耗时）
            if (detectMediaType)
            {
                try
                {
                    using var part = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                    foreach (ManagementObject p in part.Get())
                    {
                        string? idxStr = p["DiskIndex"]?.ToString();
                        if (!string.IsNullOrEmpty(idxStr) && uint.TryParse(idxStr, out uint diskIdx))
                        {
                            using var dd = new ManagementObjectSearcher(
                                $"SELECT MediaType, Model, InterfaceType FROM Win32_DiskDrive WHERE Index={diskIdx}");
                            foreach (ManagementObject d in dd.Get())
                            {
                                string model = d["Model"]?.ToString() ?? "";
                                string interfaceType = d["InterfaceType"]?.ToString() ?? "";
                                object? mediaTypeObj = d["MediaType"];

                                if (!string.IsNullOrEmpty(model))
                                {
                                    if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                        model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                                        model.Contains("Solid State", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mediaType = "SSD";
                                    }
                                    else if (model.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
                                             model.Contains("Hard Disk", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mediaType = "HDD";
                                    }
                                }

                                if (mediaType == "未知" && !string.IsNullOrEmpty(interfaceType))
                                {
                                    if (interfaceType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                        interfaceType.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mediaType = "SSD";
                                    }
                                    else if (interfaceType.Contains("IDE", StringComparison.OrdinalIgnoreCase) ||
                                             interfaceType.Contains("ATA", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mediaType = "HDD";
                                    }
                                }

                                if (mediaType == "未知" && mediaTypeObj != null)
                                {
                                    if (mediaTypeObj is ushort || mediaTypeObj is int || mediaTypeObj is uint)
                                    {
                                        uint mt = Convert.ToUInt32(mediaTypeObj);
                                        mediaType = mt switch
                                        {
                                            3 => "HDD",
                                            4 => "SSD",
                                            5 => "SCM",
                                            11 => "SSD",
                                            _ => "未知"
                                        };
                                    }
                                    else if (mediaTypeObj is string strType)
                                    {
                                        if (strType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                                            strType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                                            mediaType = "SSD";
                                        else if (strType.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
                                                 strType.Contains("Hard Disk", StringComparison.OrdinalIgnoreCase))
                                            mediaType = "HDD";
                                    }
                                }

                                break;
                            }
                        }
                        break;
                    }
                }
                catch { /* 保持未知 */ }
            }

            // BitLocker 检测：DriveType 2 与 3 共用，Win32_EncryptableVolume 同样覆盖 BitLocker To Go。
            // 失败情形（无权访问 / 类不存在 / 枚举失败）由 DetectBitLockerStatus 统一映射为
            // “无权查询（需管理员权限）”，不再静默显示“未加密”。
            if (detectBitLocker)
            {
                bitlockerStatus = DetectBitLockerStatus(letter);
            }

            string display = $"大小: {sizeGB}, 类型: {mediaType}, BitLocker: {bitlockerStatus}";
            AddRow(table, InfoGroup.Disk, $"硬盘 {letter}", display);
        }

        return table;
    }
}