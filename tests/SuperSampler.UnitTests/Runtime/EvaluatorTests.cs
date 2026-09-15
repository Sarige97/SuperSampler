using System;
using System.Collections.Generic;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Runtime;

/// <summary>计算点表达式求值器与脚本执行器测试。</summary>
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

/// <summary>脚本执行器（Jint ES5.1）测试：脚本求值、参数/点位注入、超时兜底。</summary>
public class ScriptEvaluatorTests
{
    private static PointValue? Resolve(string id)
        => id == "dev1/p1" ? PointValue.Good(7.5, DateTimeOffset.UtcNow) : null;

    private static ScriptEvaluator New()
        => new(Resolve, _ => "unit-C");

    private static object? Run(string script, IReadOnlyList<object?>? args = null, ushort[]? raw = null,
        int timeoutMs = 1000)
        => New().Execute(script, args ?? Array.Empty<object?>(), raw, "dev1", "p1", timeoutMs, 64);

    [Fact]
    public void Script_returns_last_expression_value()
    {
        var result = Run("var x = 40; x + 2");
        Assert.Equal(42.0, (double)result!);
    }

    [Fact]
    public void Script_uses_args_and_raw()
    {
        var result = Run("args[0] + args[1] + raw.length", new object?[] { 1, 2 }, new ushort[] { 9, 9 });
        // 1 + 2 + 2 = 5
        Assert.Equal(5.0, (double)result!);
    }

    [Fact]
    public void Script_P_function_resolves_point()
    {
        var result = Run("P('dev1/p1') * 2");
        Assert.Equal(15.0, (double)result!);
    }

    [Fact]
    public void Script_T_function_resolves_i18n()
    {
        var result = Run("T('unit')");
        Assert.Equal("unit-C", result as string);
    }

    [Fact]
    public void Infinite_loop_times_out_and_returns_null()
    {
        var result = Run("while (true) { }", timeoutMs: 50);
        Assert.Null(result);
    }

    [Fact]
    public void Script_exception_returns_null()
    {
        var result = Run("undefinedVar.notAMethod()");
        Assert.Null(result);
    }

    [Fact]
    public void Script_closures_and_strings_work_es5()
    {
        var result = Run("var s = 'a,b,c'.split(','); s.length");
        Assert.Equal(3.0, (double)result!);
    }
}