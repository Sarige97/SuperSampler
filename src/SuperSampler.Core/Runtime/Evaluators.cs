using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Parser;
using SuperSampler.Abstractions.Values;

namespace SuperSampler.Core.Runtime;

/// <summary>
/// 计算点表达式求值器（自研，零依赖）。
/// 语法：数值字面量、四则运算、一元负号、括号、P('点位id')（取工程值）、T('i18n key')。
/// 除零与点位缺失不抛异常：返回 NaN，由调用方按 Uncertain/Bad 处理。
/// </summary>
public static class ExpressionEvaluator
{
    public static double Evaluate(
        string? expression,
        Func<string, PointValue?> resolvePoint,
        Func<string, string?> resolveI18n)
    {
        if (string.IsNullOrWhiteSpace(expression)) return double.NaN;
        return new Parser(expression, resolvePoint, resolveI18n).ParseExpression();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;
        private readonly Func<string, PointValue?> _resolvePoint;
        private readonly Func<string, string?> _resolveI18n;

        public Parser(string? text, Func<string, PointValue?> resolvePoint, Func<string, string?> resolveI18n)
        {
            _text = text ?? string.Empty;
            _resolvePoint = resolvePoint;
            _resolveI18n = resolveI18n;
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            SkipSpaces();
            while (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-'))
            {
                var op = _text[_pos++];
                var right = ParseTerm();
                value = op == '+' ? value + right : value - right;
                SkipSpaces(); // 运算后跳过空格，否则下一轮循环条件会撞在空格上提前退出
            }

            return value;
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            SkipSpaces();
            while (_pos < _text.Length && (_text[_pos] == '*' || _text[_pos] == '/'))
            {
                var op = _text[_pos++];
                var right = ParseFactor();
                value = op == '*' ? value * right : right == 0 ? double.NaN : value / right;
                SkipSpaces(); // 同上：链式乘除必须逐段跳空格
            }

            return value;
        }

        private double ParseFactor()
        {
            SkipSpaces();
            if (_pos >= _text.Length) return double.NaN;

            if (_text[_pos] == '(')
            {
                _pos++;
                var value = ParseExpression();
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == ')') _pos++;
                return value;
            }

            if (_text[_pos] == '-')
            {
                _pos++;
                return -ParseFactor();
            }

            if (Match("P"))
            {
                _pos++; // P
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == '(') _pos++;
                SkipSpaces();
                var id = ReadStringLiteral();
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == ')') _pos++;

                var resolved = _resolvePoint(id);
                if (resolved == null || !resolved.Value.IsGood || !TryToDouble(resolved.Value.Value, out var d))
                {
                    // 非数值点（bool/字符串/原始块）参与算术、坏值、或点位无值 → NaN
                    return double.NaN;
                }

                return d;
            }

            if (Match("T"))
            {
                _pos++; // T
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == '(') _pos++;
                SkipSpaces();
                var key = ReadStringLiteral();
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == ')') _pos++;
                var text = _resolveI18n(key);
                return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : double.NaN;
            }

            return ParseNumber();
        }

        /// <summary>光标处是否为指定标识符。</summary>
        private bool Match(string name)
        {
            SkipSpaces();
            if (_pos + name.Length > _text.Length) return false;
            return string.CompareOrdinal(_text, _pos, name, 0, name.Length) == 0;
        }

        /// <summary>
        /// 点位值 → double（ADR D35）：所有数值类型统一转换，不再要求「箱内恰好是 double」。
        /// 此前 float32 解出 float、int16 解出 short 会被判为类型不符 → 计算点静默变
        /// Bad(ss.reason.calculate)（findings D21）。
        /// bool 视为非数值（离散量参与算术没有意义），字符串 / raw（ushort[]）/ null 一律不可用。
        /// </summary>
        private static bool TryToDouble(object? value, out double number)
        {
            switch (value)
            {
                case byte v: number = v; return true;
                case sbyte v: number = v; return true;
                case short v: number = v; return true;
                case ushort v: number = v; return true;
                case int v: number = v; return true;
                case uint v: number = v; return true;
                case long v: number = v; return true;
                case ulong v: number = v; return true;
                case float v: number = v; return true;
                case double v: number = v; return true;
                case decimal v: number = (double)v; return true;
                default:
                    number = 0;
                    return false;
            }
        }

        private string ReadStringLiteral()
        {
            SkipSpaces();
            if (_pos < _text.Length && (_text[_pos] == '\'' || _text[_pos] == '"')) _pos++;

            var start = _pos;
            while (_pos < _text.Length && _text[_pos] != '\'' && _text[_pos] != '"') _pos++;
            var value = _text.Substring(start, _pos - start);

            if (_pos < _text.Length) _pos++; // 跳过收尾引号
            return value;
        }

        /// <summary>
        /// 数值字面量：贪心吃掉数字与小数点。**解析不出来一律返回 NaN，绝不抛**——
        /// 形如 <c>1.2.3</c> / <c>5..2</c> / <c>.</c> 的坏字面量此前会以 <see cref="FormatException"/>
        /// 穿透 <c>IDeviceManager.GetValue</c>（宿主的界面定时器里就是一次未处理异常），
        /// 与「表达式求值不抛异常、坏表达式判 Bad」的口径相悖。
        /// </summary>
        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
            {
                _pos++;
            }

            if (start == _pos) return double.NaN;

            return double.TryParse(_text.Substring(start, _pos - start), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN;
        }

        private void SkipSpaces()
        {
            while (_pos < _text.Length && _text[_pos] == ' ') _pos++;
        }
    }
}

/// <summary>
/// 一次脚本执行的结果：成功（<see cref="Value"/> 可能为 null——脚本显式返回 null/undefined）或失败
/// （超时、脚本抛异常）。失败信息只进诊断上下文，绝不向采集线程抛出。
/// </summary>
public readonly struct ScriptResult
{
    private ScriptResult(bool succeeded, object? value, string? failure, bool timedOut, bool usedBadDependency)
    {
        Succeeded = succeeded;
        Value = value;
        Failure = failure;
        TimedOut = timedOut;
        UsedBadDependency = usedBadDependency;
    }

    /// <summary>脚本是否跑完（false = 超时或抛异常）。</summary>
    public bool Succeeded { get; }

    /// <summary>最后一个表达式的值；脚本返回 null/undefined 时为 null。</summary>
    public object? Value { get; }

    /// <summary>
    /// 脚本执行期间是否**用到过坏依赖**：<c>P('id')</c> 指向的点位不存在、没有值或质量不是 Good。
    /// 这类依赖在 JS 里会被强制转换（<c>null + 1 === 1</c>），算出来的「看似合理的数」比坏值更危险
    /// （findings D50），因此调用方必须按「依赖坏 → 本点坏」处理，与表达式型 <c>P('坏点')</c> 必判 Bad 完全一致。
    /// </summary>
    public bool UsedBadDependency { get; }

    /// <summary>失败原因（仅诊断用，人类可读；不参与本地化）。</summary>
    public string? Failure { get; }

    /// <summary>失败是否由超时引起。</summary>
    public bool TimedOut { get; }

    /// <summary>成功结果。</summary>
    public static ScriptResult Ok(object? value, bool usedBadDependency = false)
        => new(true, value, null, false, usedBadDependency);

    /// <summary>失败结果（超时或脚本异常）。</summary>
    public static ScriptResult Failed(string failure, bool timedOut) => new(false, null, failure, timedOut, false);
}

/// <summary>
/// 脚本执行器：Jint 2.x（<b>ES5.1</b>）沙箱封装。每次调用用独立引擎实例，带超时保护；
/// 超时、异常一律不抛出，改为返回 <see cref="ScriptResult"/>，由调用方按 onError 策略处理，
/// 绝不阻塞采集线程。脚本正文支持两种写法（都是 ES5）：
///   ① <b>表达式</b>：最后一个表达式的值即结果，如 <c>rawValue * 0.1</c>；
///   ② <b>方法体</b>：写 <c>return</c> 的语句序列，如 <c>if (rawValue &gt; 100) { return 1; } return 0;</c>
///      ——顶层 <c>return</c> 不是合法程序，引擎会自动按函数体包裹执行（同一份正文只判定一次）。
/// 脚本里可用的绑定：
///   raw          原始寄存器数组（ushort[]，解码场景；计算点为空数组）
///   rawValue     原始数值（按 dataType/字序解出的缩放前数值；位点 bool、位域 ushort）
///   args         配置的参数数组（当前恒为空数组，保留给后续 <c>Point/Script/Args</c>）
///   P('id')      取点位工程值（坏值/缺失为 <b>undefined</b>，且置 <see cref="ScriptResult.UsedBadDependency"/>；
///                同点表用短 id，跨点表用 "deviceId/pointId"）
///   T('key')     取 i18n 文本
///   device       设备 id（字符串）
///   point        点位 id（字符串）
///   timestamp    本次采集时刻（DateTimeOffset，UTC）
/// 语法限制（ES5.1）：无箭头函数、无 let/const、无模板字符串；写了就是脚本异常 → 按 onError 处理。
/// 资源限制（ADR D41 / findings D5）：Jint 2.x <b>没有内存上限 API</b>（LimitMemory 自 Jint 3 才有），
/// <c>Execute</c> 的 <c>maxMemoryMb</c> 仅作未来预留，当前只靠超时兜底防失控——脚本内存不受限是已知边界。
/// </summary>
public sealed class ScriptEvaluator
{
    private readonly Func<string, PointValue?> _resolvePoint;
    private readonly Func<string, string?> _resolveI18n;

    /// <summary>构造脚本执行器。</summary>
    public ScriptEvaluator(Func<string, PointValue?> resolvePoint, Func<string, string?> resolveI18n)
    {
        _resolvePoint = resolvePoint;
        _resolveI18n = resolveI18n;
    }

    /// <summary>
    /// 执行脚本并返回最后一个表达式的值。超时按 <paramref name="timeoutMs"/> 中断；
    /// 超时、脚本异常都返回失败结果（不抛）。
    /// </summary>
    public ScriptResult Execute(
        string script,
        IReadOnlyList<object?> args,
        ushort[]? raw,
        string deviceId,
        string pointId,
        int timeoutMs,
        int maxMemoryMb,
        object? rawValue = null)
    {
        try
        {
            var engine = new Engine(options => options
                .TimeoutInterval(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs))));
            // 注：Jint 2.11.58 无内存上限 API（LimitMemory 自 Jint 3 才有），
            // maxMemoryMb 仅作未来预留，当前靠 timeoutMs 兜底防失控。

            // findings D50：P() 对坏值/缺失返回的是 JS **undefined** 而不是 null——
            // null 在算术里被强制转换（null + 1 === 1），坏依赖会被静默算成「看似合理的数」并判 Good，
            // 比坏值更危险；undefined 参与算术自然产出 NaN，且 `P('x') == null`（宽松相等）仍然成立，
            // 配置里「先判空再决定」的写法不受影响。同时记下「用过坏依赖」，
            // 由调用方按「依赖坏 → 本点坏」处理（与表达式型 P('坏点') 必判 Bad 同口径）。
            var usedBadDependency = false;

            engine.SetValue("args", args ?? Array.Empty<object?>());
            engine.SetValue("raw", raw ?? Array.Empty<ushort>());
            engine.SetValue("rawValue", rawValue);
            engine.SetValue("device", deviceId);
            engine.SetValue("point", pointId);
            engine.SetValue("timestamp", DateTimeOffset.UtcNow);

            engine.SetValue("P", new Func<string, JsValue>(id =>
            {
                var pv = _resolvePoint(id);
                if (pv == null || !pv.Value.IsGood)
                {
                    usedBadDependency = true;
                    return JsValue.Undefined;
                }

                return JsValue.FromObject(engine, pv.Value.Value);
            }));

            engine.SetValue("T", new Func<string, string?>(_resolveI18n));

            engine.Execute(Prepare(script));
            return ScriptResult.Ok(engine.GetCompletionValue()?.ToObject(), usedBadDependency);
        }
        catch (Exception ex)
        {
            // 脚本超时/异常：返回失败结果，调用方按 onError 策略处理，绝不阻塞采集线程
            return ScriptResult.Failed(ex.Message, IsTimeout(ex));
        }
    }

    /// <summary>
    /// 正文写法适配（判定结果按正文缓存，同一份脚本只判一次）：
    /// 顶层 <c>return</c> 不是合法程序（Jint 报 Illegal return statement），
    /// 但「只写方法体」是配置文档与样例的写法，所以——
    /// 先按原样解析；解析不过再按函数体包裹 <c>(function () { … })()</c> 解析；
    /// 两者都不过就把原样交给引擎，让它报出真实的语法错误（如箭头函数）。
    /// </summary>
    private static string Prepare(string script)
        => PreparedScripts.GetOrAdd(script, text =>
        {
            if (Parses(text)) return text;

            var wrapped = "(function () {" + Environment.NewLine + text + Environment.NewLine + "})()";
            return Parses(wrapped) ? wrapped : text;
        });

    /// <summary>脚本正文 → 实际执行源码的缓存（脚本正文来自配置，数量有界）。</summary>
    private static readonly ConcurrentDictionary<string, string> PreparedScripts = new(StringComparer.Ordinal);

    /// <summary>能否按程序解析（只做语法检查，不执行）。</summary>
    private static bool Parses(string source)
    {
        try
        {
            new JavaScriptParser().Parse(source);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 超时判定：Jint 自己抛 <c>TimeoutException</c>（命名空间随版本变化），
    /// 这里按类型名兜底，避免为一个诊断字段绑死 Jint 的内部命名空间。
    /// </summary>
    private static bool IsTimeout(Exception ex)
        => ex is TimeoutException
           || ex.GetType().Name.IndexOf("Timeout", StringComparison.Ordinal) >= 0;
}
