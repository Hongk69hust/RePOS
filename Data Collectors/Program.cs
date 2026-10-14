/*
    PC Hardware Monitoring and MCU Data Transmission
    Purpose:
- Reads hardware sensor data using LibreHardwareMonitor.
- Monitors CPU/GPU power and supported hardware sensors.
- Collects power, temperature, voltage, and fan speed information.
- Filters invalid or unsupported sensor readings.
- Serializes the collected data into JSON format.
- Sends hardware monitoring data to MCU via HTTP POST over the local network.
- Repeats the monitoring and transmission process approximately once per second.
    Author: Chu Xuân Hồng
    Time (dd/mm/yyyy): 08/10/2026
*/
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
internal class Program
{
    private const string EspUrl = "http://192.168.1.115/data"; // Enter your MCU "IP dashbroad/data"
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var pc = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false
        };

        try
        {
            pc.Open();
            Console.WriteLine("PC Monitor - LibreHardwareMonitor");
            Console.WriteLine($"ESP8266: {EspUrl}");
            while (true)
            {
                try
                {
                    UpdateAll(pc.Hardware);
                    var all = GetAllHardware(pc.Hardware).ToList();
                    var cpu = all.FirstOrDefault(
                        h => h.HardwareType == HardwareType.Cpu);
                    var gpus = all.Where(h =>
                        h.HardwareType == HardwareType.GpuNvidia ||
                        h.HardwareType == HardwareType.GpuAmd ||
                        h.HardwareType == HardwareType.GpuIntel).ToList();
                    // CPU Package Power
                    double? cpuW = FindCpuPower(cpu);
                    // GPU Power
                    double? gpuW = FindGpuPower(gpus);
                    double? totalW =
                        cpuW.HasValue && gpuW.HasValue
                            ? Math.Round(cpuW.Value + gpuW.Value, 2)
                            : null;
                    var sensors = new List<SensorDto>();
                    foreach (var hw in all)
                    {
                        bool isCpu = hw.HardwareType == HardwareType.Cpu;
                        bool isGpu =
                            hw.HardwareType == HardwareType.GpuNvidia ||
                            hw.HardwareType == HardwareType.GpuAmd ||
                            hw.HardwareType == HardwareType.GpuIntel;
                        bool isNuvoton = hw.Name.Contains(
                            "NCT6798D",
                            StringComparison.OrdinalIgnoreCase);
                        bool isBoard =
                            hw.HardwareType == HardwareType.Motherboard ||
                            hw.HardwareType == HardwareType.SuperIO;
                        foreach (var s in hw.Sensors)
                        {
                            if (!s.Value.HasValue ||
                                !float.IsFinite(s.Value.Value))
                                continue;
                            // Nuvoton
                            if (isNuvoton)
                            {
                                if (s.SensorType != SensorType.Fan ||
                                    !(s.Name.Equals(
                                          "Fan #2",
                                          StringComparison.OrdinalIgnoreCase) ||
                                      s.Name.Equals(
                                          "Fan #3",
                                          StringComparison.OrdinalIgnoreCase)))
                                    continue;
                            }
                            else
                            {
                                if (!(isCpu || isGpu || isBoard))
                                    continue;
                                if (s.SensorType != SensorType.Voltage &&
                                    s.SensorType != SensorType.Temperature &&
                                    s.SensorType != SensorType.Fan)
                                    continue;
                            }
                            if (s.SensorType == SensorType.Temperature &&
                                (s.Value.Value < 0 || s.Value.Value > 120))
                                continue;
                            if (s.SensorType == SensorType.Fan &&
                                s.Value.Value < 0)
                                continue;
                            sensors.Add(new SensorDto
                            {
                                Device = hw.Name,
                                Name = s.Name,
                                Type = s.SensorType.ToString(),
                                Value = Math.Round((double)s.Value.Value, 2),
                                Unit = GetUnit(s.SensorType)
                            });
                        }
                    }
                    var payload = new
                    {
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        cpuPowerWatts = cpuW,
                        gpuPowerWatts = gpuW,
                        totalPowerWatts = totalW,
                        sensors
                    };
                    string json = JsonSerializer.Serialize(payload);

                    Console.WriteLine(
                        $"\n[{DateTime.Now:HH:mm:ss}] " +
                        $"CPU: {Fmt(cpuW)} W | " +
                        $"GPU: {Fmt(gpuW)} W | " +
                        $"CPU + GPU: {Fmt(totalW)} W");
                    foreach (var s in sensors)
                    {
                        Console.WriteLine(
                            $"{s.Device} | {s.Name}: {s.Value:F2} {s.Unit}");
                    }
                    using var content = new StringContent(
                        json, Encoding.UTF8, "application/json");
                    using var response = await Http.PostAsync(EspUrl, content);
                    Console.WriteLine($"HTTP {(int)response.StatusCode}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
                await Task.Delay(1000);
            }
        }
        finally
        {
            pc.Close();
        }
    }
    private static double? FindCpuPower(IHardware? cpu)
    {
        var sensor = cpu?.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Power &&
            s.Name.Contains(
                "Package",
                StringComparison.OrdinalIgnoreCase) &&
            s.Value.HasValue &&
            float.IsFinite(s.Value.Value) &&
            s.Value.Value >= 0);
        return sensor?.Value is float v
            ? Math.Round((double)v, 2)
            : null;
    }
    private static double? FindGpuPower(List<IHardware> gpus)
    {
        string[] preferredNames =
        {
            "Total Board Power",
            "Board Power",
            "GPU Power",
            "GPU Package",
            "Package Power"
        };
        foreach (string name in preferredNames)
        {
            foreach (var gpu in gpus)
            {
                var sensor = gpu.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Power &&
                    s.Name.Contains(
                        name,
                        StringComparison.OrdinalIgnoreCase) &&
                    s.Value.HasValue &&
                    float.IsFinite(s.Value.Value) &&
                    s.Value.Value >= 0);
                if (sensor?.Value is float value)
                    return Math.Round((double)value, 2);
            }
        }
        return null;
    }
    private static string GetUnit(SensorType type)
    {
        return type switch
        {
            SensorType.Power => "W",
            SensorType.Voltage => "V",
            SensorType.Temperature => "°C",
            SensorType.Fan => "RPM",
            _ => ""
        };
    }
    private static string Fmt(double? value)
    {
        return value.HasValue ? value.Value.ToString("F2") : "N/A";
    }
    private static void UpdateAll(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            hw.Update();
            UpdateAll(hw.SubHardware);
        }
    }
    private static IEnumerable<IHardware> GetAllHardware(
        IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            yield return hw;
            foreach (var child in GetAllHardware(hw.SubHardware))
                yield return child;
        }
    }
}
internal class SensorDto
{
    public string Device { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
}