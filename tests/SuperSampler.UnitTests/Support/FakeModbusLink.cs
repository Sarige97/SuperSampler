using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SuperSampler.Drivers.Modbus;
using SuperSampler.Drivers.Modbus.Wire;

namespace SuperSampler.UnitTests.Support;

/// <summary>一次链路调用记录（读/写单/写多），供测试断言调用次数与参数。</summary>
public sealed class FakeLinkCall
{
    public FakeLinkCall(bool isWrite, bool isMulti, DataArea area, int address, int count, byte unitId)
        : this(isWrite, isMulti, area, address, count, unitId, 0, 0, 0)
    {
    }

    public FakeLinkCall(bool isWrite, bool isMulti, DataArea area, int address, int count, byte unitId,
        int timeoutMs, int retries, int retryIntervalMs)
    {
        IsWrite = isWrite;
        IsMulti = isMulti;
        Area = area;
        Address = address;
        Count = count;
        UnitId = unitId;
        TimeoutMs = timeoutMs;
        Retries = retries;
        RetryIntervalMs = retryIntervalMs;
    }

    public bool IsWrite { get; }
    public bool IsMulti { get; }
    public bool IsRead => !IsWrite;
    public DataArea Area { get; }
    public int Address { get; }
    public int Count { get; }
    public byte UnitId { get; }

    /// <summary>本次调用传入的请求超时（<c>Device@requestTimeoutMs</c> → 驱动 per-request 超时）。</summary>
    public int TimeoutMs { get; }

    /// <summary>本次调用传入的重试次数（优先级：设备 &lt;Retry&gt; &gt; 链路 &lt;Retry&gt; &gt; 全局）。</summary>
    public int Retries { get; }

    /// <summary>本次调用传入的重试间隔（毫秒）。</summary>
    public int RetryIntervalMs { get; }
}

/// <summary>故障注入规则：命中条件 + 失败形态。全部字段可选，null 表示不限定。</summary>
public sealed class FakeFaultRule
{
    /// <summary>作用于写调用（false=只作用于读）。</summary>
    public bool ForWrite { get; set; }
    public byte? UnitId { get; set; }
    public DataArea? Area { get; set; }
    public int? MinAddress { get; set; }
    public int? MaxAddress { get; set; }
    /// <summary>注入的失败形态。</summary>
    public ModbusFailureKind Kind { get; set; } = ModbusFailureKind.Timeout;
    /// <summary>Kind=Protocol 时的 Modbus 异常码（01..0B）。</summary>
    public byte ExceptionCode { get; set; }
    /// <summary>命中后先延迟再应答（毫秒），0=不延迟。</summary>
    public int DelayMs { get; set; }
    /// <summary>命中概率 0.0~1.0；未注入 Random 时按「每第 N 次命中一次」的确定性口径处理（见 RandomModulo）。</summary>
    public double Probability { get; set; } = 1.0;
    /// <summary>Probability&lt;1 且未注入 Random 时的确定性口径：每第 RandomModulo 次命中。</summary>
    public int RandomModulo { get; set; } = 2;
    /// <summary>规则最多生效次数（默认无限）。</summary>
    public int RemainingCalls { get; set; } = int.MaxValue;

    /// <summary>
    /// 命中后抛出 <see cref="IOException"/>（模拟驱动层漏包装的裸异常，findings D24）。
    /// 置位时 Kind/DelayMs 不再参与。
    /// </summary>
    public bool ThrowIo { get; set; }
}

/// <summary>
/// 假主站链路：B1 推出 IModbusLink 后的确定性测试缝（findings B1）。
/// 可编程读数据、故障注入规则、线程安全调用记录器、连接状态模拟。
/// </summary>
internal sealed class FakeModbusLink : IModbusLink
{
    private readonly object _gate = new();

    public FakeModbusLink()
    {
    }

    public FakeModbusLink(bool isOpen)
    {
        _isOpen = isOpen;
    }

    // ───────────── 应答队列与调用记录 ─────────────

    public Queue<ModbusReply> ReadReplies { get; } = new();
    public Queue<ModbusReply> WriteSingleReplies { get; } = new();
    public Queue<ModbusReply> WriteMultiReplies { get; } = new();

    public List<ushort> WriteSingleValues { get; } = new();
    public List<ushort[]> WriteMultiValues { get; } = new();
    public int ReadCalls { get; private set; }

    // ───────────── 连接状态模拟 ─────────────

    private bool _isOpen = true;

    public bool IsOpen
    {
        get { lock (_gate) return _isOpen; }
    }

    public int OpenCount { get; private set; }
    public int CloseCount { get; private set; }

    /// <summary>模拟闪断/恢复。初始即视为 Open（OpenCount=1）。</summary>
    public void SetOpen(bool open)
    {
        lock (_gate)
        {
            if (_isOpen == open) return;
            _isOpen = open;
            if (open) OpenCount++; else CloseCount++;
        }
    }

    /// <summary>
    /// 读请求前先懒重连（= 真实通道「关闭后下一次请求自动连上」的行为）。
    /// 默认 false：多数用例只关心调用序列，不需要连接状态机；两层退避的用例打开它，
    /// 才能用 OpenCount/CloseCount 成对断言「关连接 / 重连」。
    /// </summary>
    public bool ReopenOnRead { get; set; }

    // ───────────── 可编程读数据 ─────────────

    private readonly Dictionary<(byte UnitId, DataArea Area, int Address), ushort[]> _readData = new();
    private ushort[]? _defaultReadData;

    /// <summary>按 unitId+area+address 精确注册读应答数据（从该地址起连续 count 个字，不足补 0）。</summary>
    public void SetReadData(byte unitId, DataArea area, int address, params ushort[] registers)
    {
        lock (_gate) _readData[(unitId, area, address)] = (ushort[])registers.Clone();
    }

    /// <summary>未命中精确规则的读的兜底成功数据（未设置时读请求判 LinkDown）。</summary>
    public ushort[]? DefaultReadData
    {
        get { lock (_gate) return _defaultReadData; }
        set { lock (_gate) _defaultReadData = value; }
    }

    // ───────────── 故障注入 ─────────────

    public List<FakeFaultRule> FaultRules { get; } = new();

    /// <summary>Probability&lt;1 时用的随机源；null 则用确定性「每第 N 次」口径。</summary>
    public Random? RandomSource { get; set; }

    private FakeFaultRule? MatchFault(bool isWrite, DataArea area, int address, byte unitId)
    {
        lock (_gate)
        {
            foreach (var rule in FaultRules)
            {
                if (rule.ForWrite != isWrite || rule.RemainingCalls <= 0) continue;
                if (rule.UnitId.HasValue && rule.UnitId.Value != unitId) continue;
                if (rule.Area.HasValue && rule.Area.Value != area) continue;
                if (rule.MinAddress.HasValue && address < rule.MinAddress.Value) continue;
                if (rule.MaxAddress.HasValue && address > rule.MaxAddress.Value) continue;
                if (rule.Probability >= 1.0)
                {
                    rule.RemainingCalls--;
                    return rule;
                }
                if (RandomSource != null)
                {
                    if (RandomSource.NextDouble() < rule.Probability)
                    {
                        rule.RemainingCalls--;
                        return rule;
                    }
                }
                else
                {
                    _ruleSeen.TryGetValue(rule, out var seen);
                    _ruleSeen[rule] = seen + 1;
                    if (_ruleSeen[rule] % rule.RandomModulo == 0)
                    {
                        rule.RemainingCalls--;
                        return rule;
                    }
                }
            }
            return null;
        }
    }

    private readonly Dictionary<FakeFaultRule, int> _ruleSeen = new();

    // ───────────── 调用记录 ─────────────

    private readonly List<FakeLinkCall> _calls = new();

    public IReadOnlyList<FakeLinkCall> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    public int WriteSingleCallCount
    {
        get { lock (_gate) return _calls.Count(c => c.IsWrite && !c.IsMulti); }
    }

    public int WriteMultiCallCount
    {
        get { lock (_gate) return _calls.Count(c => c.IsWrite && c.IsMulti); }
    }

    public int WriteCallCount
    {
        get { lock (_gate) return _calls.Count(c => c.IsWrite); }
    }

    public void ClearCalls()
    {
        lock (_gate) _calls.Clear();
    }

    // ───────────── IModbusLink ─────────────

    public ModbusReply Read(DataArea area, int address, int count, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        if (ReopenOnRead) SetOpen(true);   // 懒重连（真实通道行为）

        lock (_gate)
        {
            ReadCalls++;
            _calls.Add(new FakeLinkCall(false, false, area, address, count, unitId, timeoutMs, retries, retryIntervalMs));
        }

        var fault = MatchFault(isWrite: false, area, address, unitId);
        if (fault != null)
        {
            if (fault.DelayMs > 0) Thread.Sleep(fault.DelayMs);
            return FailBy(fault, elapsed: fault.DelayMs);
        }

        ModbusReply? queued;
        lock (_gate) queued = ReadReplies.Count > 0 ? ReadReplies.Dequeue() : null;
        if (queued != null) return queued;

        ushort[]? data;
        lock (_gate)
        {
            _readData.TryGetValue((unitId, area, address), out var exact);
            data = exact ?? _defaultReadData;
        }
        if (data != null)
        {
            var regs = new ushort[count];
            for (var i = 0; i < count && i < data.Length; i++) regs[i] = data[i];
            return ModbusReply.Ok(regs, 0);
        }

        return ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "no read reply", 0);
    }

    public ModbusReply WriteSingle(DataArea area, int address, ushort value, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        lock (_gate)
        {
            WriteSingleValues.Add(value);
            _calls.Add(new FakeLinkCall(true, false, area, address, 1, unitId, timeoutMs, retries, retryIntervalMs));
        }

        var fault = MatchFault(isWrite: true, area, address, unitId);
        if (fault != null)
        {
            if (fault.DelayMs > 0) Thread.Sleep(fault.DelayMs);
            return FailBy(fault, elapsed: fault.DelayMs);
        }

        ModbusReply? queued;
        lock (_gate) queued = WriteSingleReplies.Count > 0 ? WriteSingleReplies.Dequeue() : null;
        if (queued != null) return queued;

        return ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "no single reply", 0);
    }

    public ModbusReply WriteMulti(DataArea area, int address, ushort[] values, byte unitId, int timeoutMs, int retries, int retryIntervalMs)
    {
        lock (_gate)
        {
            WriteMultiValues.Add((ushort[])values.Clone());
            _calls.Add(new FakeLinkCall(true, true, area, address, values.Length, unitId, timeoutMs, retries, retryIntervalMs));
        }

        var fault = MatchFault(isWrite: true, area, address, unitId);
        if (fault != null)
        {
            if (fault.DelayMs > 0) Thread.Sleep(fault.DelayMs);
            return FailBy(fault, elapsed: fault.DelayMs);
        }

        ModbusReply? queued;
        lock (_gate) queued = WriteMultiReplies.Count > 0 ? WriteMultiReplies.Dequeue() : null;
        if (queued != null) return queued;

        return ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "no multi reply", 0);
    }

    private static ModbusReply FailBy(FakeFaultRule rule, long elapsed)
    {
        if (rule.ThrowIo)
        {
            // 模拟「驱动层异常未包装、直接冒到调度层」的破坏性场景（D24 的护栏测试）
            throw new IOException("injected io failure");
        }

        return rule.Kind switch
        {
            ModbusFailureKind.Protocol => ModbusReply.Fail(ModbusFailureKind.Protocol, rule.ExceptionCode,
                "injected protocol exception", elapsed),
            ModbusFailureKind.Timeout => ModbusReply.Fail(ModbusFailureKind.Timeout, 0, "injected timeout", elapsed),
            _ => ModbusReply.Fail(ModbusFailureKind.LinkDown, 0, "injected link down", elapsed),
        };
    }

    /// <summary>
    /// 关闭连接（调度器链路级退避的「关闭连接 → 等待 → 重连」第一步）。
    /// FakeModbusLink 用同一套开关计数，因此 CloseCount 就是「重建过几次连接」的证据。
    /// </summary>
    public void Close() => SetOpen(false);

    public void Dispose()
    {
    }
}
