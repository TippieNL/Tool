using SysMon.Core.Monitoring;
using Xunit;

namespace SysMon.Core.Tests;

public class SensorNamingTests
{
    [Theory]
    [InlineData("Warning Temperature")]   // NVMe threshold reported as a temperature sensor
    [InlineData("Critical Temperature")]
    [InlineData("Temperature Threshold")]
    [InlineData("Power Limit")]
    [InlineData("GPU Slowdown Temperature")]
    [InlineData("Shutdown Temperature")]
    [InlineData("Target Temperature")]
    public void ThresholdSensorsAreRecognised(string name) =>
        Assert.True(SensorNaming.IsThresholdSensor(name));

    [Theory]
    [InlineData("GPU Core")]
    [InlineData("GPU Hot Spot")]
    [InlineData("Core (Tctl/Tdie)")]
    [InlineData("Temperature")]
    [InlineData("CPU Package")]
    [InlineData("Core Max")]   // a real reading, despite reading like a bound
    public void RealReadingsAreNotTreatedAsThresholds(string name) =>
        Assert.False(SensorNaming.IsThresholdSensor(name));

    [Theory]
    [InlineData("Temperature")]
    [InlineData("temperature")]
    [InlineData("Temp")]
    public void GenericNamesAreRecognisedSoTheyCanBeQualified(string name) =>
        Assert.True(SensorNaming.IsGenericTemperatureName(name));

    [Theory]
    [InlineData("GPU Hot Spot")]
    [InlineData("Core (Tctl/Tdie)")]
    [InlineData("Temperature 2")]
    public void DescriptiveNamesStandOnTheirOwn(string name) =>
        Assert.False(SensorNaming.IsGenericTemperatureName(name));
}
