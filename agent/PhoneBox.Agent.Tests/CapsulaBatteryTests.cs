using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public class CapsulaBatteryTests
{
    [Fact]
    public void DevicesFromCapsulaAreShownAndCleanedUp()
    {
        var b = new BatteryMonitor(NullLogger<BatteryMonitor>.Instance);
        Assert.Empty(b.Snapshot());

        b.ReportExternal([
            new DeviceBattery("JBL Quantum 350", 64, "headphones", false),
            new DeviceBattery("  ", 50, "mouse", null),          // без имени — пропускаем
            new DeviceBattery("Unknown", 150, "mouse", null),    // процент вне 0–100 — пропускаем
            new DeviceBattery("Pad", 30, "toaster", true),       // неизвестный вид — other
        ]);

        var list = b.Snapshot();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, d => d is { Name: "JBL Quantum 350", Level: 64, Kind: "headphones" });
        Assert.Contains(list, d => d is { Name: "Pad", Kind: "other", Charging: true });
    }
}
