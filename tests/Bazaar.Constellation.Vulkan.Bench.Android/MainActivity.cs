using System.Globalization;
using System.IO;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Bazaar.Constellation.Vulkan;
using Bazaar.Constellation.Vulkan.Bench;

namespace Bazaar.Constellation.Vulkan.BenchHead;

// Phone-tier Constellation draw benchmark (doc constellation-scale-and-lod, section 6 phone column).
// Runs once on launch in a background thread and writes CSV rows to logcat under the tag "BazaarVulkanBench".
// Pull them with: adb logcat -s BazaarVulkanBench
// Phone caps: resident node data at most 256 MB (16M nodes at 16 bytes), at most 300 000 instances per frame.
[Activity(Label = "Constellation Vulkan bench", MainLauncher = true, NoHistory = false)]
public sealed class MainActivity : Activity
{
    private const string Tag = "BazaarVulkanBench";
    private const int WarmupFrames = 30;
    private const int MeasuredFrames = 200;
    private const long PhoneResidentBytes = 256L * 1024 * 1024;
    private const int PhoneInstanceCap = 300_000;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Thread thread = new(() => RunBench(this));
        thread.Start();
    }

    private static void RunBench(Context context)
    {
        try
        {
            VulkanProbeResult probe = VulkanProbe.Run();
            Log.Info(Tag, $"probe.available={probe.Available} reason=\"{probe.Reason}\" instance_api={probe.InstanceApiVersion}");
            foreach (VulkanDeviceInfo device in probe.Devices)
            {
                Log.Info(Tag, $"probe.device name=\"{device.Name}\" type={device.DeviceType} api={device.ApiVersion} mesh_shader={device.MeshShaderExtension}");
            }

            if (!probe.Available)
            {
                return;
            }

            string shaders = CopyShaders(context);
            using var bench = new NodeBenchmark();
            bench.Initialize(shaders);
            Log.Info(Tag, $"gpu.name=\"{bench.DeviceName}\" driver={bench.DriverVersion} vulkan={bench.ApiVersion} device_local_gib={(bench.DeviceLocalBytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture)}");
            Log.Info(Tag, "section,total_nodes,resident_nodes,instances_drawn,upload_ms,resident_bytes,wall_p50_ms,wall_p95_ms,wall_max_ms,gpu_p50_ms,gpu_p95_ms");

            long residentCap = PhoneResidentBytes / NodeBenchmark.NodeBytes;
            foreach (long total in new long[] { 1_000_000, 4_000_000, 16_000_000 })
            {
                long resident = Math.Min(total, residentCap);
                (double uploadMs, long bytes) = bench.UploadNodes(resident);
                int drawn = (int)Math.Min(resident, PhoneInstanceCap);
                Row("nodes", total, resident, drawn, uploadMs, bytes, bench.Measure(drawn, WarmupFrames, MeasuredFrames));
            }

            (double uploadAll, long bytesAll) = bench.UploadNodes(residentCap);
            foreach (int drawn in new[] { 100_000, 300_000, 1_000_000 })
            {
                if (drawn > residentCap)
                {
                    continue;
                }

                Row("instances", residentCap, residentCap, drawn, uploadAll, bytesAll, bench.Measure(drawn, WarmupFrames, MeasuredFrames));
            }

            Log.Info(Tag, "done");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"bench failed: {ex}");
        }
    }

    private static void Row(string section, long total, long resident, int drawn, double uploadMs, long bytes, FrameStats s)
    {
        Log.Info(Tag, string.Join(',', section, total, resident, drawn, uploadMs.ToString("F1", CultureInfo.InvariantCulture), bytes,
            s.WallP50Ms.ToString("F3", CultureInfo.InvariantCulture), s.WallP95Ms.ToString("F3", CultureInfo.InvariantCulture),
            s.WallMaxMs.ToString("F3", CultureInfo.InvariantCulture), s.GpuP50Ms.ToString("F3", CultureInfo.InvariantCulture),
            s.GpuP95Ms.ToString("F3", CultureInfo.InvariantCulture)));
    }

    // Shaders ship as APK assets; the benchmark reads files, so copy them once into app storage.
    private static string CopyShaders(Context context)
    {
        string directory = Path.Combine(context.FilesDir!.AbsolutePath, "Shaders");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "nodes.vert.spv", "nodes.frag.spv" })
        {
            using Stream source = context.Assets!.Open($"Shaders/{name}");
            using FileStream target = File.Create(Path.Combine(directory, name));
            source.CopyTo(target);
        }

        return directory;
    }
}
