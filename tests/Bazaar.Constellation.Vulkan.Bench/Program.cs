using System.Globalization;
using Bazaar.Constellation.Vulkan;
using Bazaar.Constellation.Vulkan.Bench;

// GPU benchmark for the Constellation node path (doc constellation-scale-and-lod, section 7).
// Usage: Bazaar.Constellation.Vulkan.Bench [resultsFile]
// Prints the Vulkan probe, then runs a node-count sweep and an instance-count sweep, and writes CSV lines.

VulkanProbeResult probe = VulkanProbe.Run();
Console.WriteLine($"probe.available={probe.Available} reason=\"{probe.Reason}\" instance_api={probe.InstanceApiVersion}");
foreach (VulkanDeviceInfo device in probe.Devices)
{
    Console.WriteLine($"probe.device name=\"{device.Name}\" type={device.DeviceType} api={device.ApiVersion} mesh_shader={device.MeshShaderExtension}");
}

if (!probe.Available)
{
    return 2;
}

const int WarmupFrames = 30;
const int MeasuredFrames = 200;
long[] nodeCounts = [1_000_000, 10_000_000, 100_000_000, 1_000_000_000];
int[] instanceSweep = [100_000, 1_000_000, 2_000_000, 10_000_000, 50_000_000, 100_000_000];
long residentCap = NodeBenchmark.ResidentBudgetBytes / NodeBenchmark.NodeBytes;

var lines = new List<string>
{
    "section,total_nodes,resident_nodes,instances_drawn,upload_ms,resident_bytes,fits_budget,wall_p50_ms,wall_p95_ms,wall_max_ms,gpu_p50_ms,gpu_p95_ms",
};

string shaders = Path.Combine(AppContext.BaseDirectory, "Shaders");
using var bench = new NodeBenchmark();
bench.Initialize(shaders);
Console.WriteLine($"gpu.name=\"{bench.DeviceName}\" driver={bench.DriverVersion} vulkan={bench.ApiVersion} device_local_gib={(bench.DeviceLocalBytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture)}");

if (args.Length > 0 && args[0] == "experiments")
{
    // Cliff investigation at 10M resident nodes. Each row varies one thing: draw-call splitting or quad size.
    bench.UploadNodes(10_000_000);
    RunExperiment(bench, "single_10M_quad_0.0015", 0, 0.0015f, 10_000_000);
    RunExperiment(bench, "single_10M_quad_0", 0, 0f, 10_000_000);
    bench.PointMode = true;
    RunExperiment(bench, "points_10M", 0, 0f, 10_000_000);
    bench.PositionScale = 0.1f;
    RunExperiment(bench, "points_10M_cluster_scale_0.1", 0, 0f, 10_000_000);
    bench.PositionScale = 0.01f;
    RunExperiment(bench, "points_10M_cluster_scale_0.01", 0, 0f, 10_000_000);
    bench.PointMode = false;
    RunExperiment(bench, "tris_10M_cluster_scale_0.01", 0, 0.0015f, 10_000_000);
    bench.PositionScale = 1f;
    RunExperiment(bench, "split_2M_chunks_10M_quad_0.0015", 2_000_000, 0.0015f, 10_000_000);
    RunExperiment(bench, "split_1M_chunks_10M_quad_0.0015", 1_000_000, 0.0015f, 10_000_000);
    foreach (int n in new[] { 4_100_000, 4_194_304, 4_200_000, 4_500_000, 5_000_000 })
    {
        RunExperiment(bench, $"single_{n}_quad_0.0015", 0, 0.0015f, n);
    }

    return 0;
}

// Sweep 1: total node count. The resident window is capped at the 1.5 GiB budget; draws are capped at 2M instances.
foreach (long total in nodeCounts)
{
    long resident = Math.Min(total, residentCap);
    (double uploadMs, long bytes) = bench.UploadNodes(resident);
    int drawn = (int)Math.Min(resident, NodeBenchmark.InstanceCap);
    FrameStats stats = bench.Measure(drawn, WarmupFrames, MeasuredFrames);
    string row = Line("nodes", total, resident, drawn, uploadMs, bytes, total * NodeBenchmark.NodeBytes <= NodeBenchmark.ResidentBudgetBytes, stats);
    lines.Add(row);
    Console.WriteLine(row);
}

// Sweep 2: instance count drawn per frame, with 100M resident nodes.
{
    (double uploadMs, long bytes) = bench.UploadNodes(residentCap);
    foreach (int drawn in instanceSweep)
    {
        if (drawn > residentCap)
        {
            continue;
        }

        FrameStats stats = bench.Measure(drawn, WarmupFrames, MeasuredFrames);
        string row = Line("instances", residentCap, residentCap, drawn, uploadMs, bytes, true, stats);
        lines.Add(row);
        Console.WriteLine(row);
    }
}

if (args.Length > 0)
{
    File.WriteAllLines(args[0], lines);
}

return 0;

static void RunExperiment(NodeBenchmark bench, string label, int chunk, float quad, int instances)
{
    bench.DrawChunk = chunk;
    bench.QuadSize = quad;
    FrameStats s = bench.Measure(instances, 10, 60);
    Console.WriteLine(string.Join(',', "experiment", label, instances,
        s.WallP50Ms.ToString("F3", CultureInfo.InvariantCulture), s.WallP95Ms.ToString("F3", CultureInfo.InvariantCulture),
        s.GpuP50Ms.ToString("F3", CultureInfo.InvariantCulture), s.GpuP95Ms.ToString("F3", CultureInfo.InvariantCulture)));
}

static string Line(string section, long total, long resident, int drawn, double uploadMs, long bytes, bool fits, FrameStats s) =>
    string.Join(',', section, total, resident, drawn, uploadMs.ToString("F1", CultureInfo.InvariantCulture), bytes, fits,
        s.WallP50Ms.ToString("F3", CultureInfo.InvariantCulture), s.WallP95Ms.ToString("F3", CultureInfo.InvariantCulture), s.WallMaxMs.ToString("F3", CultureInfo.InvariantCulture),
        s.GpuP50Ms.ToString("F3", CultureInfo.InvariantCulture), s.GpuP95Ms.ToString("F3", CultureInfo.InvariantCulture));
