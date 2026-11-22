using System.Diagnostics;
using System.Globalization;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// Renders a clustered scene through the scene adapter (<see cref="ConstellationNodeStyles.BuildColoured"/>) to an
/// offscreen image on the physical GPU, reads the pixels back and counts the pixels of each cluster colour.
/// Implements docs/design/constellation-scale-and-lod.md (sections 5 and 10: GPU frame time and offscreen readback).
/// Skips where the loader finds no usable device.
/// </summary>
public class VulkanClusterColourReadbackTests
{
    /// <summary>Environment variable that names a file to receive the per-cluster pixel counts and frame times. Unset means no file.</summary>
    private const string _reportVariable = "BAZAAR_VULKAN_CLUSTER_OUT";

    private const uint _size = 512;
    private const int _clusters = 8;
    private const int _nodesPerCluster = 6;
    private const float _radius = 0.08f;
    private const int _tolerance = 2;

    [Fact]
    public void Every_cluster_colour_appears_in_the_readback_with_the_expected_pixel_area()
    {
        VulkanOffscreenRenderer renderer;
        try
        {
            renderer = VulkanOffscreenRenderer.Create(_size, _size);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            return;
        }

        using (renderer)
        {
            // 8 clusters on a 4x2 grid of cells; each cluster has 6 nodes on a 3x2 sub-grid, so discs never overlap.
            int nodeCount = _clusters * _nodesPerCluster;
            var positions = new float[nodeCount * 2];
            var clusterIds = new int[nodeCount];
            for (int cluster = 0; cluster < _clusters; cluster++)
            {
                float cellX = -0.75f + (cluster % 4) * 0.5f;
                float cellY = -0.5f + (cluster / 4) * 1.0f;
                for (int k = 0; k < _nodesPerCluster; k++)
                {
                    int node = cluster * _nodesPerCluster + k;
                    positions[node * 2] = cellX + ((k % 3) - 1) * 0.16f;
                    positions[node * 2 + 1] = cellY + ((k / 3) - 0.5f) * 0.2f;
                    clusterIds[node] = cluster;
                }
            }

            var scene = new ConstellationScene(nodeCount, ReadOnlyMemory<int>.Empty, clusterIds);
            NodeStyleTable styles = ConstellationNodeStyles.BuildColoured(scene, NodeStyle.Create(1f, 1f, 1f, 1f, _radius));
            NodeStylePalette palette = NodeStylePalette.Build(styles);
            renderer.SetStyles(NodeStylePacking.Pack(palette));
            var instances = new NodeInstanceBuffer();
            instances.Fill(positions, 2, palette);

            renderer.Render((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
            byte[] pixels = renderer.ReadPixels().ToArray();

            // Expected area per cluster: nodes * pi * r^2 in pixels (NDC spans 2 units over _size pixels).
            double pixelsPerUnit = _size / 2.0;
            double expected = _nodesPerCluster * Math.PI * Math.Pow(_radius * pixelsPerUnit, 2);
            var counts = new int[_clusters];
            for (int cluster = 0; cluster < _clusters; cluster++)
            {
                NodeStyle colour = ConstellationNodeStyles.ForCluster(cluster);
                byte r = ToByte(colour.Red), g = ToByte(colour.Green), b = ToByte(colour.Blue);
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    if (Math.Abs(pixels[offset] - b) <= _tolerance
                        && Math.Abs(pixels[offset + 1] - g) <= _tolerance
                        && Math.Abs(pixels[offset + 2] - r) <= _tolerance)
                    {
                        counts[cluster]++;
                    }
                }
            }

            var report = new List<string> { $"device={renderer.DeviceName}; target={_size}x{_size}; clusters={_clusters}; nodes={nodeCount}; expected_pixels_per_cluster={expected:F0}" };
            for (int cluster = 0; cluster < _clusters; cluster++)
            {
                report.Add($"cluster {cluster}: pixels={counts[cluster]}");
            }

            // Frame times.
            if (renderer.TimestampsSupported)
            {
                const int warmup = 30;
                const int measured = 200;
                for (int i = 0; i < warmup; i++)
                {
                    renderer.RenderTimed((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
                }

                var gpu = new double[measured];
                var cpu = new double[measured];
                for (int i = 0; i < measured; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    gpu[i] = renderer.RenderTimed((0.05f, 0.06f, 0.08f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
                    cpu[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }

                Array.Sort(gpu);
                Array.Sort(cpu);
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"frames={measured}; gpu_p50_ms={Pct(gpu, 0.5):F4}; gpu_p95_ms={Pct(gpu, 0.95):F4}; gpu_max_ms={gpu[^1]:F4}; wall_p50_ms={Pct(cpu, 0.5):F4}; wall_p95_ms={Pct(cpu, 0.95):F4}; wall_max_ms={cpu[^1]:F4}"));
            }
            else
            {
                report.Add("timestamps not supported on this device");
            }

            string? path = Environment.GetEnvironmentVariable(_reportVariable);
            if (!string.IsNullOrWhiteSpace(path))
            {
                File.WriteAllLines(path, report);
            }

            for (int cluster = 0; cluster < _clusters; cluster++)
            {
                Assert.True(counts[cluster] > 0, $"cluster {cluster} colour never appears in the readback");
                // Edge pixels are anti-aliased and blended, so the exact-colour count is a little under the geometric area.
                Assert.InRange(counts[cluster], (int)(expected * 0.8), (int)(expected * 1.05));
            }
        }
    }

    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private static double Pct(double[] sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
}
