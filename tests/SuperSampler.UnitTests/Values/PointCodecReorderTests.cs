using System;
using System.Linq;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.UnitTests.Values;

/// <summary>字节序重排的纯函数测试：四种排布 × 2 字寄存器 / 4 字寄存器。</summary>
public class PointCodecReorderTests
{
    private static ushort[] Reorder(ushort[] input, SwapMode swap) => PointCodec.Reorder(input, swap);

    private static string Hex(ushort[] words) => string.Join(",", words.Select(w => w.ToString("X4")));

    [Theory]
    [InlineData(SwapMode.None, "1234,5678")]      // ABCD：不动
    [InlineData(SwapMode.Byte, "3412,7856")]      // BADC：逐字倒字节
    [InlineData(SwapMode.Word, "5678,1234")]      // CDAB：相邻字成对交换（2 字=整体反转）
    [InlineData(SwapMode.WordByte, "7856,3412")]  // DCBA：字内倒字节 + 整体反字
    public void Reorder_32bit_word_pairs(SwapMode swap, string expected)
    {
        var result = Reorder(new ushort[] { 0x1234, 0x5678 }, swap);
        Assert.Equal(expected, Hex(result));
    }

    /// <summary>4 字情形按 HA 模型：Word(CDAB)=相邻字成对交换；WordByte(DCBA)=逐字倒字节+整体反字。</summary>
    [Theory]
    [InlineData(SwapMode.None, "0123,4567,89AB,CDEF")]
    [InlineData(SwapMode.Byte, "2301,6745,AB89,EFCD")]   // BADC：逐字倒字节
    [InlineData(SwapMode.Word, "4567,0123,CDEF,89AB")]   // CDAB：相邻字成对交换 [w1,w0,w3,w2]
    [InlineData(SwapMode.WordByte, "EFCD,AB89,6745,2301")]// DCBA：字内倒字节 + 整体反字
    public void Reorder_64bit_ha_model(SwapMode swap, string expected)
    {
        var input = new ushort[] { 0x0123, 0x4567, 0x89AB, 0xCDEF };
        Assert.Equal(expected, Hex(Reorder(input, swap)));
    }

    /// <summary>解/编码用同一个 Reorder，四种排布全是自反（对合）变换：两次=原样。</summary>
    [Theory]
    [InlineData(SwapMode.None)]
    [InlineData(SwapMode.Byte)]
    [InlineData(SwapMode.Word)]
    [InlineData(SwapMode.WordByte)]
    public void Reorder_is_involutive(SwapMode swap)
    {
        var input = new ushort[] { 0x1122, 0x3344, 0x5566, 0x7788 };
        Assert.Equal(input, Reorder(Reorder(input, swap), swap));
    }

    /// <summary>单寄存器 16 位：None/Word 字数不变；Byte/WordByte 逐字节交换。</summary>
    [Theory]
    [InlineData(SwapMode.None, "1234")]
    [InlineData(SwapMode.Word, "1234")]      // 相邻字成对交换：1 个元素不变
    [InlineData(SwapMode.Byte, "3412")]      // 字内字节交换
    [InlineData(SwapMode.WordByte, "3412")]  // 字内字节交换 + 反转（1 元素反转=原样）
    public void Reorder_single_word(SwapMode swap, string expected)
    {
        Assert.Equal(expected, Hex(Reorder(new ushort[] { 0x1234 }, swap)));
    }
}