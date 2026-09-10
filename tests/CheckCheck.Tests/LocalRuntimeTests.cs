using System.Reflection;
using CheckCheck.Core;

internal static class LocalRuntimeTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            count++;
            if (!condition) throw new InvalidOperationException("FAILED: " + message);
        }
        IReadOnlyList<(string Id, string Name)> Parse(string text) =>
            (IReadOnlyList<(string, string)>)typeof(LocalModelRuntime)
                .GetMethod("ParseDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text])!;

        var hybrid = Parse("Available devices:\r\n  Vulkan0: AMD Radeon(TM) Graphics (8143 MiB, 7735 MiB free)\r\n  Vulkan1: NVIDIA GeForce RTX 3050 Ti Laptop GPU (3962 MiB, 3367 MiB free)\r\n");
        Check(hybrid.Count == 2 && hybrid[0].Id == "Vulkan1", "Hybrid laptops prefer a capable discrete GPU over shared graphics");
        Check(hybrid[0].Name == "NVIDIA GeForce RTX 3050 Ti Laptop GPU", "Device label retains the actual hardware name");
        var busy = Parse("Vulkan0: AMD Radeon(TM) Graphics (8143 MiB, 7735 MiB free)\nVulkan1: NVIDIA GeForce RTX 3050 Ti (3962 MiB, 300 MiB free)");
        Check(busy[0].Id == "Vulkan0", "An occupied discrete GPU is not forced over available shared graphics");
        var other = Parse("Vulkan0: Intel Graphics (8192 MiB, 7500 MiB free)\nVulkan1: AMD Radeon RX 6700 (10240 MiB, 9500 MiB free)");
        Check(other[0].Id == "Vulkan1", "Discrete AMD graphics is eligible");
        Check(Parse("Available devices:\nNo Vulkan devices found.").Count == 0, "Missing GPU is never presented as verified acceleration");
        using var runtime = new LocalModelRuntime();
        Check(!runtime.IsReady, "Installation alone does not imply a warm inference server");
        runtime.Dispose();
        Check(!runtime.IsReady, "Disposed runtime is never ready");
        return count;
    }
}
