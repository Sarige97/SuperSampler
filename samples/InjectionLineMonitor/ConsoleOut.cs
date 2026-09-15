using System;

namespace InjectionLineMonitor;

/// <summary>
/// 控制台输出闸门：轮询显示线程与操作脚本线程都会打屏，必须串行化，
/// 否则一屏点位表会被写结果/报警行切成半截（现场最难看的日志形态）。
/// </summary>
internal static class ConsoleOut
{
    private static readonly object Gate = new object();

    /// <summary>独占一行打印。</summary>
    public static void Line(string text)
    {
        lock (Gate)
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>打印一段多行文本（整体占锁，保证不被其它线程插入）。</summary>
    public static void Block(string text)
    {
        lock (Gate)
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>可空值的安全显示（undefined / null 一律显示占位符）。</summary>
    public static string Show(string? text) => string.IsNullOrEmpty(text) ? "-" : text!;
}
