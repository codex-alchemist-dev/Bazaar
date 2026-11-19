using System.Text.RegularExpressions;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoPrometheusRendererTests
{
    [Fact]
    public void WritesHelpTypeAndSamplesForEachFamily()
    {
        string text = KodoPrometheusRenderer.RenderSnapshot(
            KodoExportTestData.Snapshot(
                Bazaar.Kodo.Evaluator.KodoState.Healthy,
                score: 100,
                components: KodoExportTestData.Component("host.disk.free", ProbeStatus.Green)));

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("# TYPE kodo_state gauge", lines);
        Assert.Contains("# HELP kodo_state Kodo's displayed health state: 1 for the active state, 0 for the others.", lines);
        Assert.Contains("kodo_state{state=\"healthy\"} 1", lines);
        Assert.Contains("kodo_state{state=\"frozen\"} 0", lines);
        Assert.Contains("kodo_underlying_state{state=\"healthy\"} 1", lines);
        Assert.Contains("# TYPE kodo_score gauge", lines);
        Assert.Contains("kodo_score 100", lines);
        Assert.Contains("# TYPE kodo_probe_status gauge", lines);
        Assert.Contains("kodo_probe_status{probe_id=\"host.disk.free\",module_id=\"kodo\"} 0", lines);
    }

    [Fact]
    public void EachFamilyHasOneHelpAndOneTypeLine()
    {
        string text = KodoPrometheusRenderer.RenderSnapshot(KodoExportTestData.Snapshot(Bazaar.Kodo.Evaluator.KodoState.Healthy));

        foreach (string name in KodoMetricNames.All.Select(KodoMetricNames.ToPrometheus))
        {
            Assert.Equal(1, CountLines(text, "# HELP " + name + " "));
            Assert.Equal(1, CountLines(text, "# TYPE " + name + " gauge"));
        }
    }

    [Fact]
    public void EscapesBackslashQuoteAndNewlineInLabelValues()
    {
        KodoMetricFamily family = new(
            KodoMetricNames.ProbeStatus,
            "help",
            "1",
            [new KodoMetricPoint(0, [new KodoMetricLabel(KodoMetricLabels.ProbeId, "a\"b\\c\nd")])]);

        string text = KodoPrometheusRenderer.Render([family]);

        Assert.Contains("kodo_probe_status{probe_id=\"a\\\"b\\\\c\\nd\"} 0", text);
        string[] probeLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(probeLines, line => line.StartsWith("kodo_probe_status{", StringComparison.Ordinal));
    }

    [Fact]
    public void EscapesBackslashAndNewlineInHelpText()
    {
        Assert.Equal("line one\\nline two \\\\ end \"quoted\"", KodoPrometheusRenderer.EscapeHelp("line one\nline two \\ end \"quoted\""));
    }

    [Fact]
    public void EscapeLabelValueEscapesTheThreeSpecialCharacters()
    {
        Assert.Equal("\\\\ \\\" \\n", KodoPrometheusRenderer.EscapeLabelValue("\\ \" \n"));
    }

    [Fact]
    public void LabelsCarryOnlyTheAllowedNames()
    {
        string text = KodoPrometheusRenderer.RenderSnapshot(
            KodoExportTestData.Snapshot(
                Bazaar.Kodo.Evaluator.KodoState.Failing,
                Bazaar.Kodo.Evaluator.KodoState.Failing,
                30,
                KodoExportTestData.Component("host.disk.free", ProbeStatus.Red),
                KodoExportTestData.Component("db.postgres.ping", ProbeStatus.Unknown)));

        IEnumerable<string> names = Regex.Matches(text, "([A-Za-z_][A-Za-z0-9_]*)=\"")
            .Select(match => match.Groups[1].Value)
            .Distinct();

        Assert.All(names, name => Assert.Contains(name, KodoMetricLabels.Allowed));
        Assert.DoesNotContain("agent_id", text);
        Assert.DoesNotContain("gc_id", text);
    }

    [Fact]
    public void RefusesAFamilyThatIsNotACataloguedName()
    {
        KodoMetricFamily family = new("kodo.agent.cost", "help", "1", []);

        Assert.Throws<ArgumentException>(() => KodoPrometheusRenderer.Render([family]));
    }

    [Fact]
    public void RefusesALabelThatIsNotOnTheAllowList()
    {
        KodoMetricFamily family = new(
            KodoMetricNames.ProbeStatus,
            "help",
            "1",
            [new KodoMetricPoint(0, [new KodoMetricLabel("gc_id", "gc-1")])]);

        Assert.Throws<ArgumentException>(() => KodoPrometheusRenderer.Render([family]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RefusesANonFiniteValue(double value)
    {
        KodoMetricFamily family = new(KodoMetricNames.Score, "help", "1", [new KodoMetricPoint(value, [])]);

        Assert.Throws<ArgumentException>(() => KodoPrometheusRenderer.Render([family]));
    }

    [Fact]
    public void FormatsFractionalValuesWithInvariantCulture()
    {
        KodoMetricFamily family = new(KodoMetricNames.Score, "help", "1", [new KodoMetricPoint(0.5, [])]);

        string text = KodoPrometheusRenderer.Render([family]);

        Assert.Contains("kodo_score 0.5\n", text);
    }

    [Fact]
    public void EmptyFamilyListRendersNothing()
    {
        Assert.Equal(string.Empty, KodoPrometheusRenderer.Render([]));
    }

    private static int CountLines(string text, string prefix) =>
        text.Split('\n').Count(line => line.StartsWith(prefix, StringComparison.Ordinal));
}
