using System;
using SuperSampler.Abstractions.Errors;
using SuperSampler.Abstractions.Events;
using Xunit;

namespace SuperSampler.UnitTests.Errors;

/// <summary>错误模型测试：分类由标记接口唯一确定、异常码携带、上下文摘要。</summary>
public class ErrorModelTests
{
    private static ErrorInfo Info() => new(
        "MODBUS.EXCEPTION.02",
        EventCategory.Device,
        EventLevel.Error,
        ErrorSource.Device,
        "ss.error.illegalAddress",
        new ErrorContext(DeviceId: "Mold01", UnitId: 1, PointId: "setTemp", Address: 100, FunctionCode: 0x03));

    [Theory]
    [InlineData(typeof(TimeoutError), ErrorClass.Transient)]
    [InlineData(typeof(LinkError), ErrorClass.LinkDown)]
    [InlineData(typeof(ProtocolError), ErrorClass.Transient)]
    [InlineData(typeof(DecodeError), ErrorClass.Permanent)]
    [InlineData(typeof(WriteIndeterminateError), ErrorClass.Indeterminate)]
    public void Marker_interface_maps_to_error_class(Type type, ErrorClass expected)
    {
        var error = (IErrorEvent)Activator.CreateInstance(type, Info())!;

        Assert.Equal(expected, ErrorClasses.Classify(error));
    }

    [Fact]
    public void Policy_error_carries_policy_name()
    {
        var e = new PolicyError(Info(), "Budget");

        Assert.Equal("Budget", e.PolicyName);
        Assert.Equal(ErrorClass.Policy, ErrorClasses.Classify(e));
    }

    [Fact]
    public void Config_error_carries_config_path()
    {
        var e = new ConfigError(Info(), "points[mold.setTemp].address");

        Assert.Equal("points[mold.setTemp].address", e.ConfigPath);
        Assert.Equal(ErrorClass.Permanent, ErrorClasses.Classify(e));
    }

    [Fact]
    public void Device_exception_carries_modbus_code()
    {
        var e = new DeviceExceptionError(Info(), 0x02);

        Assert.Equal(0x02, e.ExceptionCode);
        Assert.Equal("MODBUS.EXCEPTION.02", e.Info.Code);
        Assert.Equal(EventLevel.Error, e.Level);
        Assert.Equal(EventCategory.Device, e.Category);
    }

    [Fact]
    public void Context_summary_contains_key_fields()
    {
        var s = Info().Context.ToString();

        Assert.Contains("device=Mold01", s);
        Assert.Contains("unit=1", s);
        Assert.Contains("point=setTemp", s);
        Assert.Contains("fc=0x03", s);
    }
}
