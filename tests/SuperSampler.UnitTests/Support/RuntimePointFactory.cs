using System;
using System.Collections.Generic;
using SuperSampler.Core.Config;
using SuperSampler.Core.Runtime;

namespace SuperSampler.UnitTests.Support;

/// <summary>测试用具：快速构造 RuntimePoint（编解码测试无需完整设备配置）。</summary>
internal static class RuntimePointFactory
{
    public static RuntimePoint Point(Action<RuntimePointConfig>? configure = null, string deviceId = "dev1")
    {
        var cfg = new RuntimePointConfig();
        configure?.Invoke(cfg);

        var device = new DeviceConfig
        {
            Id = deviceId,
            UnitId = 1,
            Transport = "tcp1",
            PointSetId = "ps1",
        };

        return new RuntimePoint(cfg.Build(), device);
    }

    /// <summary>点位定义承载（可变），便于按需设属性。</summary>
    public sealed class RuntimePointConfig
    {
        public string Id { get; set; } = "p";
        public RuntimeArea Area { get; set; } = RuntimeArea.HoldingRegister;
        public RuntimeDataType DataType { get; set; } = RuntimeDataType.UInt16;
        public SwapMode Swap { get; set; } = SwapMode.None;
        public int Address { get; set; } = 0;
        public int Length { get; set; }
        public int? Bit { get; set; }
        public string? BitRange { get; set; }
        public ScaleConfig? Scale { get; set; }
        public FormatConfig? Format { get; set; }
        public int BcdDigits { get; set; } = 4;
        public string DateTimeFormat { get; set; } = "plc6";
        public List<AlarmConfig> Alarms { get; } = new();

        public PointConfig Build()
        {
            var point = new PointConfig
            {
                Id = Id,
                Area = Area,
                DataType = DataType,
                Swap = Swap,
                // 测试用具在点位级显式给 swap：声明为「已声明」，运行期不再走设备级兜底（ADR D38）
                HasSwapDeclared = true,
                Address = Address,
                Length = Length,
                Bit = Bit,
                BitRange = BitRange,
                Scale = Scale,
                Format = Format,
                BcdDigits = BcdDigits,
                DateTimeFormat = DateTimeFormat,
            };
            point.Alarms.AddRange(Alarms);
            return point;
        }
    }
}