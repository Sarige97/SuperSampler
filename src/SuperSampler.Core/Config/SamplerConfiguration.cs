using System;
using System.Collections.Generic;

namespace SuperSampler.Core.Config;

/// <summary>Modbus 数据区（运行时）。</summary>
public enum RuntimeArea
{
    Coil = 0,
    DiscreteInput = 1,
    InputRegister = 2,
    HoldingRegister = 3,
}

/// <summary>点位数据类型（运行时）。</summary>
public enum RuntimeDataType
{
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
    Bcd,
    DateTime,
    Raw,
}

/// <summary>
/// 字序。语义（32 位值、原始寄存器序列 r0,r1）：
/// None=ABCD 大端；Byte=BADC 字节交换；Word=CDAB 字交换（国内最常见）；WordByte=DCBA 小端。
/// 配置里的 ABCD/BADC/CDAB/DCBA 是别名，加载时换算到这四个值。
/// </summary>
public enum SwapMode
{
    None = 0,
    Byte = 1,
    Word = 2,
    WordByte = 3,
}

/// <summary>越界钳制模式。</summary>
public enum ClampMode
{
    None = 0,
    Low = 1,
    High = 2,
    Both = 3,
}

/// <summary>数值换算：简写形式（factor/offset）或双点映射（原始量程 → 工程量程）。</summary>
public sealed class ScaleConfig
{
    public double Factor { get; set; } = 1.0;
    public double Offset { get; set; }

    /// <summary>双点映射的原始低值；四个值齐备时优先于 factor/offset。</summary>
    public double? RawLow { get; set; }
    public double? RawHigh { get; set; }
    public double? ScaledLow { get; set; }
    public double? ScaledHigh { get; set; }

    public ClampMode Clamp { get; set; } = ClampMode.None;
    public double ClampLow { get; set; }
    public double ClampHigh { get; set; }

    /// <summary>按配置把原始值换算为工程值。</summary>
    public double Apply(double raw)
    {
        double value;
        if (RawLow.HasValue && RawHigh.HasValue && ScaledLow.HasValue && ScaledHigh.HasValue)
        {
            var span = RawHigh.Value - RawLow.Value;
            value = span == 0
                ? ScaledLow.Value
                : ScaledLow.Value + (raw - RawLow.Value) * (ScaledHigh.Value - ScaledLow.Value) / span;
        }
        else
        {
            value = raw * Factor + Offset;
        }

        return Clamp switch
        {
            ClampMode.Low => Math.Max(value, ClampLow),
            ClampMode.High => Math.Min(value, ClampHigh),
            ClampMode.Both => Math.Min(Math.Max(value, ClampLow), ClampHigh),
            _ => value,
        };
    }

    /// <summary>写入前的逆换算：工程值 → 原始值。</summary>
    public double Reverse(double engineering)
    {
        if (RawLow.HasValue && RawHigh.HasValue && ScaledLow.HasValue && ScaledHigh.HasValue)
        {
            var span = ScaledHigh.Value - ScaledLow.Value;
            return span == 0
                ? RawLow.Value
                : RawLow.Value + (engineering - ScaledLow.Value) * (RawHigh.Value - RawLow.Value) / span;
        }

        return Factor == 0 ? 0 : (engineering - Offset) / Factor;
    }
}

/// <summary>显示格式化配置。</summary>
public sealed class FormatConfig
{
    public int Decimals { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public bool Thousands { get; set; }

    /// <summary>Map 的匹配基准：engineering（默认，缩放后的工程值）或 raw（缩放前的原始值）。</summary>
    public string MapOn { get; set; } = "engineering";

    /// <summary>值 → 文本映射（键为整数字符串，布尔点用 true/false）。</summary>
    public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);

    /// <summary>时间显示格式（仅 DateTime 点）。</summary>
    public string? Pattern { get; set; }
}

/// <summary>写入约束。能不能写看点位 access；这里只管「写的约束与授权」。</summary>
public sealed class WriteConfig
{
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public string? Permission { get; set; }

    /// <summary>写后是否回读校验。</summary>
    public bool Verify { get; set; }

    /// <summary>是否需二次确认（宿主界面消费；findings W22 接线）。</summary>
    public bool Confirm { get; set; }

    /// <summary>点动脉冲：写入 true 后经该毫秒数自动写回 false（线圈用；findings W22 接线）。</summary>
    public int PulseMs { get; set; }
}

/// <summary>报警定义（v1 只解析存储，判定评估后续迭代实现）。</summary>
public sealed class AlarmConfig
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "high";
    public double? Limit { get; set; }
    public int DelayMs { get; set; }
    public double Deadband { get; set; }
    public string? Priority { get; set; }
    public string? Message { get; set; }
    public bool Latch { get; set; }
    public bool AckRequired { get; set; }
}

/// <summary>非连续寄存器片段。</summary>
public sealed class SliceConfig
{
    public int Address { get; set; }
    public int Length { get; set; }
}

/// <summary>
/// 点位运行时配置：已解析（PLC 编址换算、模板与 Defaults 继承、i18n 替换均已完成）。
/// </summary>
public sealed class PointConfig
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string DeviceId { get; set; } = string.Empty;

    public RuntimeArea Area { get; set; } = RuntimeArea.HoldingRegister;
    public int Address { get; set; }

    /// <summary>占用寄存器数；0 表示由 DataType 推导。</summary>
    public int Length { get; set; }

    public RuntimeDataType DataType { get; set; } = RuntimeDataType.UInt16;
    public SwapMode Swap { get; set; } = SwapMode.Word;

    /// <summary>
    /// 点位是否**显式声明**了 swap（Point@swap / PointSet/Defaults@swap / 块内点位随 Block@swap）。
    /// false 表示「待兜底」：运行期按所属设备解析为 <see cref="DeviceConfig.Swap"/>（→ Global@swap）。
    /// 之所以不在解析期固化设备级兜底：同一个 PointSet 可被多个设备共用，而各设备字序可以不同（ADR D38）。
    /// </summary>
    public bool HasSwapDeclared { get; set; }

    public int? Bit { get; set; }

    /// <summary>位域，形如 4-7（含端点）；null 表示不取位域。</summary>
    public string? BitRange { get; set; }

    /// <summary>非连续片段；null 表示地址连续。</summary>
    public IReadOnlyList<SliceConfig>? Slices { get; set; }

    public string Access { get; set; } = "read";
    public string? Unit { get; set; }
    public string ScanGroup { get; set; } = "normal";
    public int? UnitIdOverride { get; set; }

    public ScaleConfig? Scale { get; set; }
    public FormatConfig? Format { get; set; }
    public WriteConfig? Write { get; set; }
    public List<AlarmConfig> Alarms { get; } = new();

    // ---- 字符串点 ----
    public string StringEncoding { get; set; } = "ascii";
    public bool StringTrimNull { get; set; } = true;

    // ---- BCD 点 ----
    public int BcdDigits { get; set; } = 4;

    // ---- 时间点 ----
    public string DateTimeFormat { get; set; } = "plc6";

    /// <summary>计算点标记（不占地址、由表达式求值）。</summary>
    public bool IsCalculated { get; set; }

    /// <summary>计算点表达式（仅 IsCalculated=true）。</summary>
    public string? Expression { get; set; }

    /// <summary>是否启用；false 的点不参与轮询（findings W17 接线）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>说明（留档/界面提示）。</summary>
    public string? Desc { get; set; }

    /// <summary>只读原因说明（仅界面提示，不影响逻辑）。</summary>
    public string? ReadonlyBy { get; set; }

    /// <summary>工程量程下限/上限（"0..300" 解析而来，供界面刻度与百分比死区）。</summary>
    public double? RangeLow { get; set; }

    public double? RangeHigh { get; set; }

    /// <summary>点位级脚本正文（&lt;Script&gt; 元素）；非空时用脚本解码替代标准编解码（findings W20 接线）。</summary>
    public string? Script { get; set; }

    /// <summary>脚本超时覆盖（毫秒），null 用全局默认。</summary>
    public int? ScriptTimeoutMs { get; set; }

    /// <summary>脚本出错策略覆盖（markBad/keepLast/defaultValue），null 用全局默认。</summary>
    public string? ScriptOnError { get; set; }

    public bool IsWritable => string.Equals(Access, "write", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Access, "readwrite", StringComparison.OrdinalIgnoreCase);
}

/// <summary>块读配置：一次请求读一段连续地址，块内点位共享结果。</summary>
public sealed class BlockConfig
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public RuntimeArea Area { get; set; } = RuntimeArea.HoldingRegister;
    public int Start { get; set; }
    public int Count { get; set; }
    public string ScanGroup { get; set; } = "normal";
    public int? UnitId { get; set; }
    public SwapMode Swap { get; set; } = SwapMode.Word;

    /// <summary>是否启用；false 的块不参与轮询（findings W16 接线）。</summary>
    public bool Enabled { get; set; } = true;

    public List<PointConfig> Points { get; } = new();
}

/// <summary>扫描组。</summary>
public sealed class ScanGroupConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>poll | onDemand | once。</summary>
    public string Mode { get; set; } = "poll";

    public int RateMs { get; set; } = 1000;
    public int JitterMs { get; set; }
}

/// <summary>链路配置。</summary>
public sealed class TransportConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>tcp | rtuovertcp | rtu | ascii | udp。</summary>
    public string Variant { get; set; } = "tcp";

    public bool Enabled { get; set; } = true;
    public string? Host { get; set; }
    public int Port { get; set; } = 502;
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "none";
    public string StopBits { get; set; } = "one";
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int RequestTimeoutMs { get; set; } = 1000;
    public int GapMs { get; set; }

    // ── 串口专属（findings W13 接线）──
    public string Handshake { get; set; } = "none";
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public int ReadTimeoutMs { get; set; } = 500;
    public int WriteTimeoutMs { get; set; } = 500;

    // ── 链路级重试覆盖（findings W14 接线）──
    public int RetryCount { get; set; } = 2;
    public int RetryIntervalMs { get; set; } = 100;

    /// <summary>配置里是否显式写了 &lt;Retry&gt;；用于「设备 → 链路 → 全局」的覆盖判定。</summary>
    public bool HasRetryOverride { get; set; }
}

/// <summary>从站设备配置：一条链路上的一个从站。</summary>
public sealed class DeviceConfig
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = string.Empty;
    public int UnitId { get; set; } = 1;
    public string PointSetId { get; set; } = string.Empty;
    public string ScanGroup { get; set; } = "normal";
    public SwapMode Swap { get; set; } = SwapMode.Word;
    public int RequestTimeoutMs { get; set; } = 1000;
    public int RetryCount { get; set; } = 2;
    public int RetryIntervalMs { get; set; } = 100;
    public bool Paused { get; set; }
}

/// <summary>全局选项。</summary>
public sealed class GlobalOptions
{
    public string NullText { get; set; } = "--";
    public SwapMode DefaultSwap { get; set; } = SwapMode.Word;
    public int RequestTimeoutMs { get; set; } = 1000;
    public int DefaultRateMs { get; set; } = 1000;

    /// <summary>通信失败时值怎么办：keepLast（保留旧值）| null（置空）。</summary>
    public string OnCommErrorValue { get; set; } = "keepLast";

    /// <summary>通信失败时的质量等级：bad | offline | uncertain（findings W9 接线）。</summary>
    public string OnCommError { get; set; } = "bad";

    /// <summary>超过该时长未刷新即视为陈旧，毫秒（findings W9 接线）。</summary>
    public int StaleAfterMs { get; set; } = 5000;

    // ── 以下为 findings W5/W7/W8 接线：此前声明但未被加载器读取 ──

    /// <summary>请求级重试次数（设备可覆盖），默认 2。</summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>重试间隔毫秒。</summary>
    public int RetryIntervalMs { get; set; } = 100;

    /// <summary>重试退避策略：none | linear | exponential（v1 仅记录，用于诊断输出）。</summary>
    public string RetryBackoff { get; set; } = "exponential";

    /// <summary>连续超时多少次升级为链路故障（v1 记录，策略后续迭代）。</summary>
    public int EscalateAfter { get; set; } = 3;

    /// <summary>单设备单轮失败处理总预算毫秒（v1 记录）。</summary>
    public int BudgetMs { get; set; } = 3000;

    /// <summary>Modbus 单次读寄存器上限（协议硬上限 125）。</summary>
    public int MaxRegistersPerRead { get; set; } = 125;

    /// <summary>Modbus 单次读位上限（协议硬上限 2000）。</summary>
    public int MaxBitsPerRead { get; set; } = 2000;

    /// <summary>散点合并容差（寄存器数）；0 表示仅合并严格相邻地址。</summary>
    public int MergeGap { get; set; }

    /// <summary>脚本默认超时毫秒。</summary>
    public int ScriptTimeoutMs { get; set; } = 50;

    /// <summary>脚本默认出错策略：markBad | keepLast | defaultValue。</summary>
    public string ScriptOnError { get; set; } = "markBad";

    /// <summary>界面语言（供宿主取用）。</summary>
    public string Language { get; set; } = "zh_CN";

    /// <summary>回退语言。</summary>
    public string FallbackLanguage { get; set; } = "en_US";

    /// <summary>显示时区（供宿主取用）。</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>调试用原始读写开关（IModbusDebugTool 的 RawRead/RawWrite），默认关闭。</summary>
    public bool AllowRawAccess { get; set; }
}

/// <summary>整份运行时配置（已通过校验）。</summary>
public sealed class SamplerConfiguration
{
    public GlobalOptions Global { get; set; } = new();

    /// <summary>工程元信息（findings W2 接线：解析供宿主展示）。</summary>
    public MetaOptions Meta { get; set; } = new();

    public List<TransportConfig> Transports { get; } = new();
    public List<ScanGroupConfig> ScanGroups { get; } = new();
    public List<DeviceConfig> Devices { get; } = new();
    public List<PointSetConfig> PointSets { get; } = new();

    public I18nCatalog I18n { get; set; } = new();

    /// <summary>加载期提示（未实现段/字段等），宿主可记录或显示；findings C 类机制。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>未实现段的处理策略：warn（默认）| error | ignore。来自 HostConfig@unsupportedPolicy。</summary>
    public string UnsupportedPolicy { get; set; } = "warn";
}

/// <summary>工程元信息（说明性，不参与运行逻辑）。</summary>
public sealed class MetaOptions
{
    public string? ProjectName { get; set; }
    public string? Comment { get; set; }
    public string? Author { get; set; }
    public string? CreatedAt { get; set; }
    public int Revision { get; set; }
    public List<string> Tags { get; } = new();
}

/// <summary>点表配置。</summary>
public sealed class PointSetConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>本表点位默认值（字段为 null 表示未指定，继续向上继承）。</summary>
    public PointDefaults? Defaults { get; set; }

    public List<BlockConfig> Blocks { get; } = new();
    public List<PointConfig> Points { get; } = new();

    /// <summary>计算点（v1 解析存储，不评估）。</summary>
    public List<PointConfig> Calculated { get; } = new();
}

/// <summary>点表级默认值：字段为 null 表示未指定。</summary>
public sealed class PointDefaults
{
    public RuntimeArea? Area { get; set; }
    public RuntimeDataType? DataType { get; set; }
    public SwapMode? Swap { get; set; }
    public string? ScanGroup { get; set; }
    public int? UnitId { get; set; }
    public string? Access { get; set; }
}

/// <summary>
/// i18n 目录：KEY → 当前语言文本。加载配置时完成 ${KEY} 替换，运行期不再查表。
/// </summary>
public sealed class I18nCatalog
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public void Add(string key, string value) => _entries[key] = value;

    public string? Resolve(string key)
        => _entries.TryGetValue(key, out var value) ? value : null;

    /// <summary>把文本里的 ${KEY} 全部替换为当前语言文本；未命中的 key 原样保留。</summary>
    public string Substitute(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${")) return text;

        var result = text;
        foreach (var pair in _entries)
        {
            var token = "${" + pair.Key + "}";
            if (result.Contains(token))
            {
                result = result.Replace(token, pair.Value);
            }
        }

        return result;
    }
}
