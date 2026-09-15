# SuperSampler

C# 通用上位机采集框架（Modbus 优先，协议可插拔）。

- 目标框架：.NET Framework 4.6（零外部依赖，兼容 4.6 ~ 4.8）
- 设计文档见仓库外的 `../docs/`（设计阶段产物，暂不入仓）

## 结构

```
src/
  SuperSampler.Abstractions     纯契约：值模型、错误模型、事件、驱动接口
  SuperSampler.Core             配置加载校验、编解码、调度、事件总线、策略
  SuperSampler.Drivers.Modbus   Modbus 驱动（TCP/RTU/RTU-over-TCP/UDP/ASCII）
  SuperSampler.Hosting          DI 扩展与宿主集成
tests/
  SuperSampler.UnitTests        单元测试
  SuperSampler.IntegrationTests 集成测试（对接模拟从站）
```
