namespace SuperSampler.Drivers.Modbus.Wire;

/// <summary>Modbus 数据区（驱动层自有定义，避免驱动依赖 Core；Core 负责映射）。</summary>
public enum DataArea
{
    /// <summary>线圈（01/05/0F）。</summary>
    Coil = 0,

    /// <summary>离散输入（02）。</summary>
    DiscreteInput = 1,

    /// <summary>输入寄存器（04）。</summary>
    InputRegister = 2,

    /// <summary>保持寄存器（03/06/10）。</summary>
    HoldingRegister = 3,
}
