using System;
using System.Collections.Generic;
using System.Linq;

namespace SuperSampler.Core.Config;

/// <summary>
/// <see cref="SamplerConfigLoader"/> 的位映射展开部分（docs/01 §4.1、docs/11 §一.4）。
///
/// <c>&lt;Bits&gt;</c> 里的 <c>&lt;Bit index&gt;</c> / <c>&lt;Field from to&gt;</c> 在**加载期**展开成子点位，
/// 命名 <c>整字点位id.位名</c>；整数值点位**保留**（两种读法都在）。
/// 展开出的子点位是货真价实的点位：进注册表、参与自动分组、能订阅值变化、能进报表与调试输出；
/// 但**不挂报警、不做任何业务判断**（用户明确要求：框架只负责取值，不解释数据含义）。
///
/// 展开时机：一个点表全部点位解析完之后（子点位 id 冲突要拿全表比对），全量校验之前
/// （这样 CGV-17「同址同位重复声明」天然覆盖展开出来的位声明）。
/// 规则编号 CGV-27，见 Config/配置字段说明.md 第 16 节。
/// </summary>
public static partial class SamplerConfigLoader
{
    /// <summary>位映射只能挂在单字整数点位上（位只存在于第一个寄存器）。</summary>
    private static readonly RuntimeDataType[] BitMapOwnerTypes =
    {
        RuntimeDataType.Bool, RuntimeDataType.Int16, RuntimeDataType.UInt16,
    };

    /// <summary>展开一个点表里的全部位映射（散点与块内点位各回各的列表）。</summary>
    internal static void ExpandBitMaps(PointSetConfig set, List<string> errors)
    {
        // 展开过程会往同一个列表追加子点位，先快照（子点位没有 Bits，天然不会递归）
        foreach (var owner in set.Points.ToList())
        {
            Expand(set, set.Points, owner, errors);
        }

        foreach (var block in set.Blocks)
        {
            foreach (var owner in block.Points.ToList())
            {
                Expand(set, block.Points, owner, errors);
            }
        }

        // 计算点不占地址：写 <Bits> 是误配，报错（不做静默忽略）
        foreach (var calculated in set.Calculated)
        {
            if (calculated.Bits != null)
            {
                errors.Add($"{PointPath(set.Id, calculated.Id)}：计算点不得声明 <Bits>（不占地址，没有位可取）");
            }
        }
    }

    private static void Expand(PointSetConfig set, List<PointConfig> target, PointConfig owner, List<string> errors)
    {
        if (owner.Bits == null) return;

        var path = PointPath(set.Id, owner.Id);

        // ── 结构互斥（CGV-27）：整字点位本身必须「干净」，否则无从取位 ──
        if (owner.Bit.HasValue || owner.BitRange != null)
        {
            errors.Add($"{path}：已声明 bit/bitRange 但又有 <Bits>（同一寄存器两种取位写法互斥，请只保留一种）");
            return;
        }

        if (owner.Slices != null)
        {
            errors.Add($"{path}：已声明 <Slices> 但又有 <Bits>（片段点位没有「整字」可取，二者互斥）");
            return;
        }

        if (!BitMapOwnerTypes.Contains(owner.DataType) || EffectiveLength(owner) != 1)
        {
            errors.Add($"{path}：<Bits> 只能挂在单字整数点位上（dataType 应为 bool/int16/uint16 且不跨字，"
                + $"当前 dataType={owner.DataType} 占 {EffectiveLength(owner)} 字）");
            return;
        }

        if (owner.Address < 0) return;   // 地址非法已由 CGV-18 报出，这里不重复报、也不展开

        // 全表 id 命名空间（散点 + 块内 + 计算点 + 本次展开出来的）
        var ids = new HashSet<string>(AllPointIds(set), StringComparer.Ordinal);

        foreach (var entry in owner.Bits)
        {
            var entryWhere = $"{path} 的 <Bits> 条目 {Describe(entry)}";

            // 位范围：0..15 且 from ≤ to（条目解析期的缺属性/非数值已报过，这里只判范围）
            if (entry.Index.HasValue || entry.From.HasValue || entry.To.HasValue)
            {
                var from = entry.BitFrom;
                var to = entry.BitTo;
                if (from < 0 || from > 15 || to < 0 || to > 15 || from > to)
                {
                    errors.Add($"{entryWhere}：位索引非法（应为 0 ≤ from ≤ to ≤ 15，Bit 的 index 同理）");
                    continue;
                }
            }

            if (entry.Name.Length == 0) continue;   // 缺 name 已在条目校验里报过

            var childId = owner.Id + "." + entry.Name;
            if (!ids.Add(childId))
            {
                errors.Add($"{path}：位映射展开出的子点位 id {childId} 与现有点位冲突"
                    + "（子点位 id = 整字点位id.位名，必须点表内唯一）");
                continue;
            }

            target.Add(BuildChild(set, owner, entry, childId));
        }
    }

    /// <summary>按「整字点位 + 条目」造子点位：能力是普通点位的子集（值、订阅、报表），但不带报警与业务判断。</summary>
    private static PointConfig BuildChild(PointSetConfig set, PointConfig owner, BitMapEntry entry, string childId)
    {
        var child = new PointConfig
        {
            Id = childId,
            PointSetId = set.Id,
            ParentPointId = owner.Id,
            Name = entry.Text ?? entry.Name,
            Desc = "位映射展开自 " + owner.Id,

            // 取值口径与整字点位完全一致（同一寄存器、同一从站、同一节奏）
            Area = owner.Area,
            Address = owner.Address,
            DataType = entry.IsSingleBit ? RuntimeDataType.Bool : RuntimeDataType.UInt16,
            Bit = entry.IsSingleBit ? entry.Index : null,
            BitRange = entry.IsSingleBit ? null : entry.BitFrom + "-" + entry.BitTo,
            Swap = owner.Swap,
            HasSwapDeclared = owner.HasSwapDeclared,
            Unit = owner.Unit,
            UnitIdOverride = owner.UnitIdOverride,

            // 可写性继承整字点位（写入走位读改写，只翻目标位/位域）
            Access = owner.Access,

            IntervalMs = owner.IntervalMs,
            Mode = owner.Mode,
            Enabled = owner.Enabled,

            // 注意：刻意不复制 Scale / Write / Alarms —— 框架只负责取值，不解释位与位域的含义
        };

        if (!entry.IsSingleBit && entry.Map.Count > 0)
        {
            var format = new FormatConfig();
            foreach (var pair in entry.Map) format.Map[pair.Key] = pair.Value;
            child.Format = format;
        }

        return child;
    }

    /// <summary>点表内的全部点位 id（散点 + 块内 + 计算点）。</summary>
    private static IEnumerable<string> AllPointIds(PointSetConfig set)
        => set.Points.Select(p => p.Id)
            .Concat(set.Blocks.SelectMany(b => b.Points).Select(p => p.Id))
            .Concat(set.Calculated.Select(p => p.Id));

    /// <summary>条目的可读描述（报错定位用）。</summary>
    private static string Describe(BitMapEntry entry)
        => entry.IsSingleBit
            ? $"Bit index={entry.Index}{(entry.Name.Length > 0 ? " name=" + entry.Name : string.Empty)}"
            : $"Field {entry.From}-{entry.To}{(entry.Name.Length > 0 ? " name=" + entry.Name : string.Empty)}";
}
