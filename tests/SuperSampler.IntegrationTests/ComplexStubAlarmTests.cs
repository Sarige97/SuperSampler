using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using SuperSampler.Abstractions.Events;
using SuperSampler.Abstractions.Facade;
using SuperSampler.Abstractions.Values;
using SuperSampler.Core.Runtime;
using Xunit;

namespace SuperSampler.IntegrationTests;

/// <summary>
/// C3 报警闭环：越限 → 延时到点 → 触发 → 确认 → 回差清除 → 锁存不重触发；
/// 另加「坏值期间挂起」（断路器制造通讯故障，坏值不参与报警评估）。
///
/// 驱动量：ENV-01（unit 6，P3:16004）4x@2「温度报警上限」——镜像脚本只读不写，
/// 写进去就稳定保持，适合做报警的驱动量（等价于把可写设定值当模拟量用）。
/// </summary>
[Collection(ComplexStubCollection.Name)]
public sealed class ComplexStubAlarmTests
{
    private readonly ComplexStubFixture _stub;

    public ComplexStubAlarmTests(ComplexStubFixture stub) => _stub = stub;

    private const int Neutral = 200;   // 复位值：低于 limit-deadband，保证报警处于 Normal

    // ─────────────────────── C3-1：完整闭环 ───────────────────────

    [SkippableFact]
    public async Task C3_AlarmClosedLoop_DelayDeadbandLatchAck()
    {
        _stub.RequireStub();

        var i18nDir = _stub.WorkSubDir("i18n");
        var langPath = Path.Combine(i18nDir, "alarm.lang");
        File.WriteAllText(langPath, "alarm.overtemp = 料筒温度超限\n", new UTF8Encoding(false));

        // limit=300 / deadband=50 / delayMs=300 / latch+ackRequired → 锁存语义
        var point = ComplexStubFixture.Point("env.limit", 2, "int16",
            "area=\"holding\" access=\"readwrite\" swap=\"abcd\"",
            "<Write min=\"0\" max=\"500\" />" +
            "<Alarm id=\"A-TEMP\" type=\"high\" limit=\"300\" delayMs=\"300\" deadband=\"50\"" +
            " priority=\"P1\" message=\"${alarm.overtemp}\" latch=\"true\" ackRequired=\"true\" />");

        var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                  "<Global><Retry count=\"0\" /><Polling rateMs=\"200\" requestTimeoutMs=\"3000\" /></Global>" +
                  "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"200\" /></ScanGroups>" +
                  "<I18n><Files><File path=\"" + langPath.Replace("\\", "/") + "\" /></Files></I18n>" +
                  "<AlarmClasses><AlarmClass id=\"P1\" /></AlarmClasses>" +
                  "<Transports>" + ComplexStubFixture.Transport("p3", _stub.SimPortP3) + "</Transports>" +
                  "<Devices>" + ComplexStubFixture.Device("env", "p3", 6, "ps") + "</Devices>" +
                  "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + point + "</Points></PointSet></PointSets>" +
                  "</SamplerConfig>";

        using var engine = new SamplerEngine(_stub.LoadConfig(xml));
        engine.Start();
        using var raised = new ComplexStubFixture.EventCollector<AlarmRaisedEvent>(engine.Bus);
        using var cleared = new ComplexStubFixture.EventCollector<AlarmClearedEvent>(engine.Bus);
        using var acked = new ComplexStubFixture.EventCollector<AlarmAcknowledgedEvent>(engine.Bus);
        try
        {
            await ComplexStubFixture.WaitGoodAsync(engine, "env", "env.limit");

            // ① 复位到报警带内、限值以下：不该有触发
            Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", Neutral)).Outcome);
            await Task.Delay(600);
            Assert.Equal(0, raised.Count);

            // ② 越限：延时到点后才触发（delayMs=300）
            var writeAt = DateTime.UtcNow;
            Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", 400)).Outcome);
            Assert.True(await raised.WaitCountAsync(1, 4000), "越限后应在延时到点触发");
            var elapsed = DateTime.UtcNow - writeAt;
            Assert.True(elapsed >= TimeSpan.FromMilliseconds(300),
                "delayMs=300：触发不应早于写入 +300ms（实测 " + elapsed.TotalMilliseconds.ToString("F0") + "ms）");

            var raiseEvent = raised.Items.Single();
            Assert.Equal("A-TEMP", raiseEvent.AlarmId);
            Assert.Equal("env", raiseEvent.DeviceId);
            Assert.Equal("env.limit", raiseEvent.PointId);
            Assert.Equal("high", raiseEvent.AlarmType);
            Assert.Equal(300d, raiseEvent.Limit);
            Assert.Equal(400, Convert.ToInt32(raiseEvent.Value));
            Assert.Equal("P1", raiseEvent.Priority);
            Assert.Equal("料筒温度超限", raiseEvent.Message);   // i18n 已替换

            // ③ 回差带内不算清除：limit-deadband = 250，260 仍在报警侧（D4 方向性）
            Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", 260)).Outcome);
            await Task.Delay(700);
            Assert.Equal(0, cleared.Count);

            // ④ 回到正常区（< 250）→ 清除
            Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", Neutral)).Outcome);
            Assert.True(await cleared.WaitCountAsync(1, 4000), "回落到回差之外应清除");
            var clearEvent = cleared.Items.Single();
            Assert.Equal("A-TEMP", clearEvent.AlarmId);
            Assert.Equal("high", clearEvent.AlarmType);
            Assert.Equal("env.limit", clearEvent.PointId);

            // ⑤ 锁存 + 未确认：再次越限不重触发
            Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", 400)).Outcome);
            await Task.Delay(900);
            Assert.Equal(1, raised.Count);

            // ⑥ 确认：框架内部有 Acknowledge，但 SamplerEngine/IDeviceManager/IModbusDebugTool 都没有公开通路
            //    （findings F6）——这里用反射直接调 AlarmEngine.Acknowledge，把「确认 → 重新武装」钉死。
            AcknowledgeAlarmViaReflection(engine, "env", "env.limit", "A-TEMP", "op-ac");
            Assert.True(await acked.WaitCountAsync(1, 2000), "确认应发 AlarmAcknowledgedEvent");
            var ackEvent = acked.Items.Single();
            Assert.Equal("A-TEMP", ackEvent.AlarmId);
            Assert.Equal("op-ac", ackEvent.User);
            Assert.Equal("env", ackEvent.DeviceId);

            // 确认后仍在越限状态 → 状态机重新触发
            Assert.True(await raised.WaitCountAsync(2, 4000), "确认后再次越限应重新触发");
            Assert.Equal(2, raised.Count);
        }
        finally
        {
            await engine.SetValueAsync("env", "env.limit", Neutral);
            engine.Dispose();
        }
    }

    /// <summary>
    /// 反射调用 Scheduler 内部 AlarmEngine.Acknowledge（public API 未暴露，见 findings F6）。
    /// </summary>
    private static void AcknowledgeAlarmViaReflection(SamplerEngine engine, string deviceId, string pointId,
        string alarmId, string user)
    {
        var schedulerField = typeof(SamplerEngine).GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(schedulerField != null, "SamplerEngine._scheduler 字段不存在（框架内部结构变化）");
        var scheduler = schedulerField!.GetValue(engine);
        Assert.NotNull(scheduler);

        var alarmField = scheduler!.GetType().GetField("_alarms", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(alarmField != null, "Scheduler._alarms 字段不存在（框架内部结构变化）");
        var alarmEngine = alarmField!.GetValue(scheduler);
        Assert.NotNull(alarmEngine);

        var method = alarmEngine!.GetType().GetMethod("Acknowledge", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method != null, "AlarmEngine.Acknowledge 不存在（框架内部结构变化）");
        method!.Invoke(alarmEngine, new object[] { deviceId, pointId, alarmId, user });
    }

    // ─────────────────────── C3-2：坏值期间挂起（不断言误报） ───────────────────────

    [SkippableFact]
    public async Task C3_AlarmEvaluationIsSuspendedWhileQualityIsBad()
    {
        _stub.RequireBreaker();

        var publicPort = ComplexStubFixture.PublicBasePort + 2;   // 2504 → P3-VERIFY（ENV-01 unit 6）
        await _stub.ClearAllAsync();
        try
        {
            var point = ComplexStubFixture.Point("env.limit", 2, "int16",
                "area=\"holding\" access=\"readwrite\" swap=\"abcd\"",
                "<Write min=\"0\" max=\"500\" />" +
                "<Alarm id=\"A-BAD\" type=\"high\" limit=\"300\" deadband=\"50\" />");

            var xml = "<SamplerConfig schemaVersion=\"3.0\">" +
                      "<Global><Retry count=\"0\" /><Polling rateMs=\"200\" requestTimeoutMs=\"800\" />" +
                      "<Quality onCommError=\"bad\" onCommErrorValue=\"null\" /></Global>" +
                      "<ScanGroups><ScanGroup id=\"normal\" mode=\"poll\" rateMs=\"200\" /></ScanGroups>" +
                      "<Transports>" + ComplexStubFixture.Transport("pub", publicPort, "tcp", 800) + "</Transports>" +
                      "<Devices>" + ComplexStubFixture.Device("env", "pub", 6, "ps") + "</Devices>" +
                      "<PointSets><PointSet id=\"ps\"><Defaults swap=\"abcd\" /><Points>" + point + "</Points></PointSet></PointSets>" +
                      "</SamplerConfig>";

            using var engine = new SamplerEngine(_stub.LoadConfig(xml));
            engine.Start();
            using var raised = new ComplexStubFixture.EventCollector<AlarmRaisedEvent>(engine.Bus);
            using var cleared = new ComplexStubFixture.EventCollector<AlarmClearedEvent>(engine.Bus);
            try
            {
                await ComplexStubFixture.WaitGoodAsync(engine, "env", "env.limit");

                var ack = await _stub.InjectAsync(
                    "{\"port\":" + publicPort + ",\"unit\":6,\"action\":\"silent\"}");
                Assert.True(ack.Ok, ack.ToString());

                await ComplexStubFixture.WaitQualityAsync(engine, "env", "env.limit", PointQuality.Bad, 6000);
                await Task.Delay(1500);

                // 坏值期间不参与评估：既不误报也不误清
                Assert.Equal(0, raised.Count);
                Assert.Equal(0, cleared.Count);

                // 恢复通讯后质量回到 Good，评估继续（此时值仍在正常区 → 仍无报警）
                await _stub.OnlineAsync(publicPort, 6);
                await ComplexStubFixture.WaitQualityAsync(engine, "env", "env.limit", PointQuality.Good, 8000);
                await Task.Delay(600);
                Assert.Equal(0, raised.Count);
                Assert.Equal(0, cleared.Count);

                // 恢复后越限 → 报警照常触发（评估链路确实活着）
                Assert.Equal(WriteOutcome.Succeeded, (await engine.SetValueAsync("env", "env.limit", 400)).Outcome);
                Assert.True(await raised.WaitCountAsync(1, 5000), "通讯恢复后越限应照常报警");
            }
            finally
            {
                await engine.SetValueAsync("env", "env.limit", Neutral);
                engine.Dispose();
            }
        }
        finally
        {
            await _stub.ClearAllAsync();
        }
    }
}
