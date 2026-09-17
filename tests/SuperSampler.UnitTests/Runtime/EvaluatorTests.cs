using System;
using System.Collections.Generic;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>计算点表达式求值器测试。</summary>
public class ExpressionEvaluatorTests
{
    private static readonly Func<string, PointValue?> NoPoints = _ => null;
    private static readonly Func<string, string?> NoText = _ => null;

    private static double Eval(string expr, Func<string, PointValue?>? points = null, Func<string, string?>? i18n = null)
        => ExpressionEvaluator.Evaluate(expr, points ?? NoPoints, i18n ?? NoText);

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("-5 + 3", -2.0)]
    [InlineData("10 - 2 - 3", 5.0)]
    [InlineData("2 * 3 * 4", 24.0)]
    [InlineData("1.5 + 0.5", 2.0)]
    public void Arithmetic_and_precedence(string expr, double expected)
    {
        Assert.Equal(expected, Eval(expr), 6);
    }

    [Fact]
    public void Unary_minus_on_parenthesized_term()
    {
        Assert.Equal(6.0, Eval("-(2 - 8)"), 6);
    }

    [Fact]
    public void Divide_by_zero_returns_nan_not_throw()
    {
        Assert.True(double.IsNaN(Eval("1 / 0")));
    }

    [Fact]
    public void P_reference_resolves_point_value()
    {
        var points = new Func<string, PointValue?>(id =>
        {
            if (id == "dev1/p1") return PointValue.Good(25.0, DateTimeOffset.UtcNow);
            return null;
        });

        Assert.Equal(35.0, Eval("P('dev1/p1') + 10", points), 6);
    }

    [Fact]
    public void P_reference_missing_point_returns_nan()
    {
        Assert.True(double.IsNaN(Eval("P('ghost/x') + 1")));
    }

    [Fact]
    public void T_reference_resolves_numeric_i18n()
    {
        Assert.Equal(3.0, Eval("T('factor')", i18n: _ => "3"), 6);
    }

    [Fact]
    public void T_reference_non_numeric_text_returns_nan()
    {
        Assert.True(double.IsNaN(Eval("T('label')", i18n: _ => "hello")));
    }

    [Fact]
    public void P_combined_with_operators_and_unary()
    {
        var points = new Func<string, PointValue?>(id =>
            id == "a/b" ? PointValue.Good(4.0, DateTimeOffset.UtcNow) : null);

        Assert.Equal(15.0, Eval("P('a/b') * 4 - 1", points), 6);
    }

    // ─────────────── D21（ADR D35）：P('id') 对所有数值类型统一取 double ───────────────

    [Theory]
    [InlineData(typeof(float), 2.5f, 3.5)]     // float32 点解出 float
    [InlineData(typeof(short), (short)-3, -2.0)] // int16 点解出 short
    [InlineData(typeof(int), 7, 8.0)]          // int32
    [InlineData(typeof(uint), 4000000000u, 4000000001.0)] // uint32（超 int 范围）
    [InlineData(typeof(ushort), (ushort)65535, 65536.0)]  // uint16
    [InlineData(typeof(long), -5000000000L, -4999999999.0)] // int64
    [InlineData(typeof(ulong), 18446744073709551615UL, 1.8446744073709552E19)] // uint64
    [InlineData(typeof(byte), (byte)200, 201.0)]
    public void P_reference_accepts_every_numeric_type(Type type, object value, double expected)
    {
        var points = new Func<string, PointValue?>(_ => PointValue.Good(value, DateTimeOffset.UtcNow));

        Assert.IsType(type, value); // 钉死本行覆盖的箱内类型（float32 出 float、int16 出 short…）
        Assert.Equal(expected, Eval("P('x') + 1", points), 6);
    }

    [Fact]
    public void P_reference_accepts_decimal_value()
    {
        // decimal 无法写进 InlineData 字面量，单独一例
        var points = new Func<string, PointValue?>(_ => PointValue.Good(1.5m, DateTimeOffset.UtcNow));

        Assert.Equal(2.5, Eval("P('x') + 1", points), 6);
    }

    [Fact]
    public void P_reference_accepts_double_from_scaled_point()
    {
        // 带 Scale 的点（float32 缩放后是 double）：修复前就可用，这里锁定不回归
        var points = new Func<string, PointValue?>(_ => PointValue.Good(3.14, DateTimeOffset.UtcNow));

        Assert.Equal(4.14, Eval("P('x') + 1", points), 6);
    }

    [Fact]
    public void P_reference_of_string_or_raw_point_is_nan()
    {
        // 字符串点与 raw 点（ushort[]）不是数值，参与算术 → NaN（不可用）
        var text = new Func<string, PointValue?>(_ => PointValue.Good("abc", DateTimeOffset.UtcNow));
        var raw = new Func<string, PointValue?>(_ => PointValue.Good(new ushort[] { 1, 2 }, DateTimeOffset.UtcNow));
        var boolean = new Func<string, PointValue?>(_ => PointValue.Good(true, DateTimeOffset.UtcNow));

        Assert.True(double.IsNaN(Eval("P('x') + 1", text)));
        Assert.True(double.IsNaN(Eval("P('x') + 1", raw)));
        Assert.True(double.IsNaN(Eval("P('x') + 1", boolean))); // bool 是离散量，不算数值
    }

    [Fact]
    public void P_reference_of_bad_quality_point_is_nan()
    {
        // 坏值仍判不可用（不管箱内是不是数值）
        var points = new Func<string, PointValue?>(_ =>
            PointValue.Bad("ss.reason.comm", DateTimeOffset.UtcNow, 12.0));

        Assert.True(double.IsNaN(Eval("P('x') + 1", points)));
    }
}

/// <summary>
/// 脚本执行器（Jint ES5.1）测试：求值、绑定注入（args/raw/rawValue/P/T）、超时与异常不抛。
/// 失败一律以 <see cref="ScriptResult"/> 表达（Succeeded=false），绝不向调用方抛。
/// </summary>
public class ScriptEvaluatorTests
{
    private static PointValue? Resolve(string id)
        => id == "dev1/p1" ? PointValue.Good(7.5, DateTimeOffset.UtcNow) : null;

    private static ScriptEvaluator New()
        => new(Resolve, _ => "unit-C");

    private static ScriptResult Run(string script, IReadOnlyList<object?>? args = null, ushort[]? raw = null,
        int timeoutMs = 1000, object? rawValue = null)
        => New().Execute(script, args ?? Array.Empty<object?>(), raw, "dev1", "p1", timeoutMs, 64, rawValue);

    [Fact]
    public void Script_returns_last_expression_value()
    {
        var result = Run("var x = 40; x + 2");

        Assert.True(result.Succeeded);
        Assert.Equal(42.0, (double)result.Value!);
    }

    [Fact]
    public void Script_uses_args_and_raw()
    {
        var result = Run("args[0] + args[1] + raw.length", new object?[] { 1, 2 }, new ushort[] { 9, 9 });

        // 1 + 2 + 2 = 5
        Assert.True(result.Succeeded);
        Assert.Equal(5.0, (double)result.Value!);
    }

    [Fact]
    public void Script_receives_raw_value_and_can_branch_on_it()
    {
        // 脚本解码的核心语义：拿缩放前的原始数值做分支/查表，返回值即工程值
        var result = Run("rawValue < 0 ? 0 : rawValue * 0.5", raw: new ushort[] { 0x000A }, rawValue: 10);

        Assert.True(result.Succeeded);
        Assert.Equal(5.0, (double)result.Value!);
    }

    [Fact]
    public void Script_can_return_string_and_bool()
    {
        Assert.Equal("RUN", Run("'RUN'").Value as string);
        Assert.Equal(true, Run("1 === 1").Value);
    }

    [Fact]
    public void Script_P_function_resolves_point()
    {
        var result = Run("P('dev1/p1') * 2");

        Assert.True(result.Succeeded);
        Assert.Equal(15.0, (double)result.Value!);
    }

    [Fact]
    public void Script_T_function_resolves_i18n()
    {
        Assert.Equal("unit-C", Run("T('unit')").Value as string);
    }

    [Fact]
    public void Infinite_loop_times_out_and_reports_failure()
    {
        var result = Run("while (true) { }", timeoutMs: 50);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Script_exception_is_returned_as_failure_not_thrown()
    {
        var result = Run("undefinedVar.notAMethod()");

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void Es51_syntax_limit_arrow_function_fails_as_script_error()
    {
        // ES5.1 没有箭头函数：写了解析就失败——按失败返回，不抛、不崩
        var result = Run("[1,2,3].map(function (x) { return x * 2; })[2]");

        Assert.True(result.Succeeded);            // 函数表达式是 ES5，合法
        Assert.Equal(6.0, (double)result.Value!);

        var arrow = Run("var f = function (x) { return x; }; f(1)");
        Assert.True(arrow.Succeeded);

        // 真箭头函数（ES6）：Jint 2.x 语法解析失败 → 失败结果（上层按 onError 处理）
        var es6 = Run("var f = (x) => x * 2; f(1)");
        Assert.False(es6.Succeeded);
        Assert.False(es6.TimedOut);
    }

    [Fact]
    public void Script_closures_and_strings_work_es5()
    {
        var result = Run("var s = 'a,b,c'.split(','); s.length");

        Assert.True(result.Succeeded);
        Assert.Equal(3.0, (double)result.Value!);
    }

    [Fact]
    public void Script_body_may_be_an_expression_or_a_method_body_with_return()
    {
        // 两种写法都是 ES5、都必须能用：表达式（最后一个表达式的值）
        Assert.Equal(42.0, (double)Run("var x = 40; x + 2").Value!);

        // 方法体（顶层 return 不是合法程序，引擎自动按函数体包裹）
        Assert.Equal(42.0, (double)Run("return 41 + 1;").Value!);
        Assert.Equal("b", Run("if (rawValue > 100) { return 'a'; } return 'b';", rawValue: 5).Value as string);
    }

    [Fact]
    public void Null_return_is_success_with_null_value()
    {
        // 脚本主动返回 null：执行成功、值为 null——"要不要判坏"由上层按 onError 策略决定
        var result = Run("var x = null; x");

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Timeout_is_capped_by_the_configured_interval()
    {
        // 超时保护必须有界：1e9 次循环的脚本不能在 50ms 里跑完，必须被切断
        var started = DateTime.UtcNow;
        var result = Run("var s = 0; for (var i = 0; i < 1000000000; i++) { s = s + i; }", timeoutMs: 50);
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.True(elapsed.TotalSeconds < 10, "脚本超时必须在秒级返回，实际 " + elapsed.TotalSeconds + "s");
    }
}
