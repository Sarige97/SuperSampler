using System;
using System.Collections.Generic;
using System.Globalization;
using Jint;
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

                var pointValue = _resolvePoint(id);
                if (pointValue == null || !pointValue.Value.TryGetValue(out double d))
                {
                    // 非数值点（字符串/原始块）参与算术，或点位无值 → NaN
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

        private double ParseNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.'))
            {
                _pos++;
            }

            return start == _pos
                ? double.NaN
                : double.Parse(_text.Substring(start, _pos - start), CultureInfo.InvariantCulture);
        }

        private void SkipSpaces()
        {
            while (_pos < _text.Length && _text[_pos] == ' ') _pos++;
        }
    }
}

/// <summary>
/// 脚本执行器：Jint 2.x（ES5.1）沙箱封装。每次调用独立引擎实例，带超时与内存上限，
/// 超时/异常返回 null 由调用方按 Global@Script@onError 处理，绝不阻塞采集线程。脚本里可用：
///   args        配置的参数数组
///   P('id')     取点位工程值（坏值/缺失为 null）
///   T('key')    取 i18n 文本
///   raw         原始寄存器数组（解码场景，ushort[]）
///   device/point 设备与点位 id（字符串）
/// 语法为 ES5.1：无箭头函数、无 let/const。
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
    /// 执行脚本并返回最后一个表达式的值；超时、异常或内存超限返回 null（不抛出）。
    /// </summary>
    public object? Execute(
        string script,
        IReadOnlyList<object?> args,
        ushort[]? raw,
        string deviceId,
        string pointId,
        int timeoutMs,
        int maxMemoryMb)
    {
        try
        {
            var engine = new Engine(options => options
                .TimeoutInterval(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs))));
            // 注：Jint 2.11.58 无内存上限 API（LimitMemory 自 Jint 3 才有），
            // maxMemoryMb 仅作未来预留，当前靠 timeoutMs 兜底防失控。

            engine.SetValue("args", args ?? Array.Empty<object?>());
            engine.SetValue("raw", raw ?? Array.Empty<ushort>());
            engine.SetValue("device", deviceId);
            engine.SetValue("point", pointId);
            engine.SetValue("timestamp", DateTimeOffset.UtcNow);

            engine.SetValue("P", new Func<string, object?>(id =>
            {
                var pv = _resolvePoint(id);
                return pv != null && pv.Value.IsGood ? pv.Value.Value : null;
            }));

            engine.SetValue("T", new Func<string, string?>(_resolveI18n));

            engine.Execute(script);
            return engine.GetCompletionValue()?.ToObject();
        }
        catch (Exception)
        {
            // 脚本超时/异常/内存超限：返回 null，调用方按 onError 策略处理，绝不阻塞采集线程
            return null;
        }
    }
}
