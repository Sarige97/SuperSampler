using System;
using System.Collections.Concurrent;
using System.Globalization;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Config;
using SuperSampler.Core.Events;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 点位脚本解码（ADR D41，2026-09-16 接线）：把**原始寄存器 / 原始数值**交给 JS（Jint，ES5.1），
/// 脚本最后一个表达式的值即工程值。框架只负责「把原始数据转成工程值」这一步，
/// 不对数据做任何业务判断（报警、量程、单位仍走各自的机制）。
///
/// 时序（解码路径，<see cref="Scheduler"/> 轮询与门面按需读共用同一实现）：
/// <list type="number">
/// <item>按点位声明切出寄存器（<c>RuntimePoint.TryExtract</c>）→ 标准解码（<see cref="PointCodec.Decode(RuntimePoint, ushort[], DateTimeOffset)"/>）；</item>
/// <item>有脚本且帧长覆盖该点位 → 调脚本（<c>raw</c> = 原始寄存器、<c>rawValue</c> = 缩放前的原始数值）；</item>
/// <item>脚本返回值即工程值（<b>不再做 Scale 换算</b>）；返回 null/undefined、超时、抛异常按 onError 处理。</item>
/// </list>
///
/// 失败与质量（onError，当前只实现 <c>markBad</c>——加载期已把其它取值判为非法）：
/// <list type="bullet">
/// <item>脚本返回 null/undefined → <c>Bad(ss.reason.scriptNull)</c>（不记错误事件：脚本主动放弃不是故障）；</item>
/// <item>脚本用到坏依赖（<c>P('id')</c> 指向的点位不存在/没有好值）→ <c>Bad(ss.reason.calculate)</c>
/// （与表达式型计算点同口径；不记错误事件——依赖坏是数据条件，不是脚本故障；findings D50）；</item>
/// <item>脚本超时 → <c>Bad(ss.reason.scriptTimeout)</c> + <c>SS.SCRIPT.TIMEOUT</c> 错误事件；</item>
/// <item>脚本抛异常（含 ES5.1 不支持的语法）→ <c>Bad(ss.reason.scriptError)</c> + <c>SS.SCRIPT.FAILED</c> 错误事件；</item>
/// <item>脚本产出非有限浮点 → 按 ADR D32 降级 <c>Uncertain</c>（<c>ss.reason.nan</c>/<c>ss.reason.infinite</c>），绝不当 Good。</item>
/// </list>
/// 错误事件按「失败种类变化才发」去重（同一点位持续超时不会每轮刷一条，恢复正常后重新武装），
/// 与调度器「一次窗口失败 = 一条错误事件」的抗风暴口径一致（docs/02 第 7 节）。
///
/// <b>绝不阻塞采集线程</b>：脚本引擎自身不抛（见 <see cref="ScriptEvaluator"/>），这里再兜一层 catch，
/// 任何意外都退化成坏值 + 一条错误事件，轮询线程继续跑下一个点位。
/// </summary>
public sealed class ScriptDecoder
{
    /// <summary>脚本返回 null/undefined 时的质量原因。</summary>
    public const string ReasonNull = "ss.reason.scriptNull";

    /// <summary>脚本超时时的质量原因。</summary>
    public const string ReasonTimeout = "ss.reason.scriptTimeout";

    /// <summary>脚本抛异常时的质量原因。</summary>
    public const string ReasonError = "ss.reason.scriptError";

    /// <summary>
    /// 脚本用到坏依赖（<c>P('id')</c> 指向的点位不存在/没有好值）时的质量原因。
    /// 与表达式型计算点同一个原因码（口径一致：依赖坏 → 本点坏，findings D50）。
    /// </summary>
    public const string ReasonBadDependency = "ss.reason.calculate";

    /// <summary>
    /// 脚本引擎的内存上限预留值（MB）。Jint 2.x 没有内存上限 API（findings D5 / ADR D41），
    /// 该值只是参数占位，当前真正的兜底是超时。
    /// </summary>
    private const int MaxMemoryMb = 64;

    private readonly InProcessEventBus _bus;
    private readonly GlobalOptions _global;
    private readonly Func<string, string, PointValue?> _resolvePoint;
    private readonly Func<string, string?> _resolveI18n;

    /// <summary>点位键 → 上次已发的失败码（去重用）；跑成功一次即清除，失败复发会重新上报。</summary>
    private readonly ConcurrentDictionary<string, string> _lastFailure = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造脚本解码器。<paramref name="resolvePoint"/> 供脚本里的 <c>P('id')</c> 取点位工程值，
    /// 签名是 <c>(deviceId, id)</c>——id 口径由实现方决定（同点表短 id 或 "deviceId/pointId" 限定名）。
    /// </summary>
    public ScriptDecoder(
        InProcessEventBus bus,
        GlobalOptions global,
        Func<string, string, PointValue?> resolvePoint,
        Func<string, string?> resolveI18n)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _global = global ?? throw new ArgumentNullException(nameof(global));
        _resolvePoint = resolvePoint ?? throw new ArgumentNullException(nameof(resolvePoint));
        _resolveI18n = resolveI18n ?? throw new ArgumentNullException(nameof(resolveI18n));
    }

    /// <summary>
    /// 解码路径应用脚本：registers 为该点位按声明切出的原始寄存器，standard 为标准解码结果（含缩放），
    /// rawValue 为标准解码给出的缩放前原始数值。
    /// 无脚本、或短帧（脚本没有可用的输入）时原样返回 <paramref name="standard"/>；
    /// 否则脚本返回值即工程值。
    /// </summary>
    public PointValue ApplyDecode(
        RuntimePoint point,
        ushort[] registers,
        PointValue standard,
        object? rawValue,
        DateTimeOffset timestamp)
    {
        if (!point.HasScript) return standard;

        // 短帧：连声明的字长都没读回来，脚本没有输入可言——按标准解码的 Bad(shortFrame) 走，不跑脚本
        if (registers.Length < point.Length) return standard;

        return Run(point, registers, rawValue, timestamp, point.Script!);
    }

    /// <summary>
    /// 计算点脚本型（<c>&lt;Calculated&gt;&lt;Point&gt;&lt;Script&gt;</c>）：无寄存器可喂，
    /// 脚本可用 <c>P('id')</c> 取其它点位值；返回值即计算点工程值。失败口径与解码路径完全一致。
    /// </summary>
    public PointValue Evaluate(RuntimePoint point, DateTimeOffset timestamp)
    {
        if (!point.HasScript)
        {
            // 计算点既无表达式也无脚本：加载期已报错（CGV-32），运行时兜底给坏值而不是静默给数
            return PointValue.Bad("ss.reason.notSupported", timestamp);
        }

        return Run(point, Array.Empty<ushort>(), null, timestamp, point.Script!);
    }

    /// <summary>脚本执行与结果落地（解码与计算点共用）。</summary>
    private PointValue Run(
        RuntimePoint point,
        ushort[] registers,
        object? rawValue,
        DateTimeOffset timestamp,
        string script)
    {
        ScriptResult result;
        try
        {
            // P('id') 的解析要带点位所属设备：同一个点表可被多设备共用，短 id 只能在设备内解析
            var evaluator = new ScriptEvaluator(id => _resolvePoint(point.DeviceId, id), _resolveI18n);
            result = evaluator.Execute(
                script,
                Array.Empty<object?>(),
                registers,
                point.DeviceId,
                point.PointId,
                point.ScriptTimeoutMs ?? _global.ScriptTimeoutMs,
                MaxMemoryMb,
                rawValue);
        }
        catch (Exception ex)
        {
            // ScriptEvaluator 契约上不抛；这里是最后一道防线：采集线程绝不能因脚本死掉
            result = ScriptResult.Failed(ex.Message, timedOut: false);
        }

        if (!result.Succeeded)
        {
            EmitFailure(point, result);
            var reason = result.TimedOut ? ReasonTimeout : ReasonError;
            return PointValue.Bad(reason, timestamp, result.Failure ?? string.Empty);
        }

        _lastFailure.TryRemove(point.Key, out _);

        if (result.Value == null)
        {
            // 脚本显式返回 null/undefined：主动放弃，不是故障 → 坏值但不记错误事件
            return PointValue.Bad(ReasonNull, timestamp);
        }

        // findings D50：脚本读到的依赖是坏值/缺失时，JS 会把 undefined 算成 NaN 或强制转换出
        // 「看似合理的数」（`null + 1 === 1`）——伪正常数比坏值更危险，绝不能判 Good。
        // 口径与表达式型一致：依赖坏 → 本点 Bad("ss.reason.calculate")（不记错误事件：这是数据条件不是脚本故障）。
        if (result.UsedBadDependency)
        {
            return PointValue.Bad(ReasonBadDependency, timestamp);
        }

        var value = Normalize(result.Value);

        // ADR D32：非有限浮点降级 Uncertain（值保留供排查，绝不当 Good）——脚本产出同样受这条门禁约束
        if (value is double d)
        {
            if (double.IsNaN(d)) return PointValue.Uncertain(value, "ss.reason.nan", timestamp, rawValue);
            if (double.IsInfinity(d)) return PointValue.Uncertain(value, "ss.reason.infinite", timestamp, rawValue);
        }

        return PointValue.Good(value, timestamp, rawValue);
    }

    /// <summary>
    /// 脚本返回值 → 值模型：数值/字符串/布尔/时间原样取用；
    /// 其余 JS 对象（数组、对象字面量）取其文本形式——值模型里不出现脚本引擎的内部类型。
    /// </summary>
    private static object Normalize(object value)
    {
        switch (value)
        {
            case double:
            case float:
            case decimal:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case bool:
            case string:
            case DateTime:
            case DateTimeOffset:
                return value;
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    /// <summary>
    /// 记一次脚本失败（超时 / 异常）。同一点位持续同一类失败只发第一条（失败种类变化或中途成功过才再发），
    /// 避免每轮刷事件；恢复后 <see cref="_lastFailure"/> 被清除，再次失败会重新上报。
    /// </summary>
    private void EmitFailure(RuntimePoint point, ScriptResult result)
    {
        var code = result.TimedOut ? "SS.SCRIPT.TIMEOUT" : "SS.SCRIPT.FAILED";
        if (_lastFailure.TryGetValue(point.Key, out var previous) && previous == code) return;
        _lastFailure[point.Key] = code;

        var info = new ErrorInfo(
            code,
            EventCategory.Device,
            EventLevel.Error,
            ErrorSource.Codec,
            result.TimedOut ? "ss.error.scriptTimeout" : "ss.error.scriptFailed",
            new ErrorContext(
                DeviceId: point.DeviceId,
                PointId: point.PointId,
                Area: point.Area.ToString(),
                Address: point.Address,
                UnitId: point.UnitId,
                ElapsedMs: 0));

        // 解码产不出值属于永久错误：规则错了重试也没用（docs/02 §2.2 的 DecodeError 族）
        _bus.Emit(new DecodeError(info));
    }
}
