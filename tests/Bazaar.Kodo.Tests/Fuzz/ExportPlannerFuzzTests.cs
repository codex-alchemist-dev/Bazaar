using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Export;
using Bazaar.Kodo.Tests.Api;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of the export planners (doc 46 sections 15.3 and 15.4): the Prometheus renderer, the OTLP mapper, the webhook
/// destination rules, envelope builder, signing key and retry planner. Each either refuses with its documented exception or gives
/// output that keeps its promises, for every seed. The identifier patterns are checked here with <c>\A</c> and <c>\z</c>, so an
/// accepted value can never carry a trailing line feed.
/// </summary>
public sealed partial class ExportPlannerFuzzTests
{
    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictComponentId();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictKeyId();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictEventId();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictIdentifierValue();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StrictName();

    // One exposition line: a comment, or name{label="value",...} value, with a quoted value that has no bare quote, backslash or line feed.
    [GeneratedRegex("^(# HELP [a-z_]+ .*|# TYPE [a-z_]+ gauge|[a-z_]+(\\{[a-z_]+=\"([^\"\\\\\\n]|\\\\.)*\"(,[a-z_]+=\"([^\"\\\\\\n]|\\\\.)*\")*\\})? \\S+)\\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ExpositionLine();

    private static readonly string[] _hostileTexts =
    [
        "plain", "a\nb", "a\"b", "a\\b", "\\", "\"", "\n", "a\r\nb", "tab\there", "{}", "=\"", "café", "😀", "x\0y", string.Empty, "end\\",
    ];

    private static string Pick(Random random, string[] values) => values[random.Next(values.Length)];

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    public void PrometheusRendererEitherRefusesOrWritesOnlyWellFormedLines(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        string[] names = [.. KodoMetricNames.All, "kodo.other", "kodo state", string.Empty, "KODO.STATE"];
        string[] labelNames = [.. KodoMetricLabels.Allowed, "instance_id", "Probe_Id", "a b", string.Empty];
        double[] values = [0, 1, -1, 3, 0.1, 1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, 123456789.125];

        for (int iteration = 0; iteration < 600; iteration++)
        {
            List<KodoMetricFamily> families = [];
            int familyCount = random.Next(0, 4);
            for (int f = 0; f < familyCount; f++)
            {
                List<KodoMetricPoint> points = [];
                int pointCount = random.Next(0, 4);
                for (int p = 0; p < pointCount; p++)
                {
                    List<KodoMetricLabel> labels = [];
                    int labelCount = random.Next(0, 3);
                    for (int l = 0; l < labelCount; l++)
                    {
                        labels.Add(new KodoMetricLabel(Pick(random, labelNames), Pick(random, _hostileTexts)));
                    }

                    points.Add(new KodoMetricPoint(values[random.Next(values.Length)], labels));
                }

                families.Add(new KodoMetricFamily(Pick(random, names), Pick(random, _hostileTexts), "1", points));
            }

            string text;
            try
            {
                text = KodoPrometheusRenderer.Render(families);
            }
            catch (ArgumentException)
            {
                continue;
            }

            Assert.Equal(text, KodoPrometheusRenderer.Render(families));
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(ExpositionLine().IsMatch(line), $"seed={seed} iteration={iteration} line={line}");
            }

            int expectedLines = families.Sum(family => 2 + family.Points.Count);
            Assert.Equal(expectedLines, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    private static KodoStatusSnapshotDto HostileSnapshot(Random random)
    {
        string[] states = ["healthy", "degraded", "failing", "recovering", "frozen", "unobserved", "Healthy", string.Empty, "healthy\n"];
        string[] probeStates = ["green", "amber", "red", "unknown", "GREEN", "purple", string.Empty];
        string[] ids = ["host.disk.free", "db.ping", "kodo.self.journal", "host.disk.free\n", "Host.Disk", "a..b", "x", "0abc", "probe-1", "abcdef0123456789abcdef.x", new string('a', 129), string.Empty, "a.b_c.d9"];
        DateTimeOffset[] times = [KodoApiTestData.Now, DateTimeOffset.UnixEpoch, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, new DateTimeOffset(2262, 4, 12, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(1677, 1, 1, 0, 0, 0, TimeSpan.Zero)];
        List<KodoComponentDto> components = [];
        int count = random.Next(0, 6);
        for (int i = 0; i < count; i++)
        {
            components.Add(new KodoComponentDto(Pick(random, ids), Pick(random, probeStates), null, KodoApiTestData.Now));
        }

        KodoStatusDto status = new(
            Pick(random, states),
            random.Next(3) == 0 ? Pick(random, states) : null,
            random.Next(-5, 120),
            times[random.Next(times.Length)],
            []);
        return new KodoStatusSnapshotDto(1, random.Next(0, 1000), status, null, components);
    }

    [Theory]
    [InlineData(311)]
    [InlineData(312)]
    [InlineData(313)]
    public void SnapshotExportsRefuseHostileSnapshotsAndAgreeWhenTheyAccept(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            KodoStatusSnapshotDto snapshot = HostileSnapshot(random);
            string where = $"seed={seed} iteration={iteration}";

            string? text = null;
            try
            {
                text = KodoPrometheusRenderer.RenderSnapshot(snapshot);
            }
            catch (ArgumentException)
            {
                // A state, status or id the export refuses.
            }

            IReadOnlyList<KodoOtlpGauge>? gauges = null;
            try
            {
                gauges = KodoOtlpMapper.Map(snapshot);
            }
            catch (ArgumentException)
            {
                // The same refusals, before the time is mapped.
            }

            Assert.True(text is null == gauges is null, $"{where}: one export accepted what the other refused");
            if (text is null)
            {
                continue;
            }

            foreach (KodoComponentDto component in snapshot.Components)
            {
                Assert.True(StrictComponentId().IsMatch(component.Id), $"{where}: exported id '{component.Id}' has a shape the export forbids");
            }

            Assert.Equal(
                gauges!.Single(gauge => gauge.Name == KodoMetricNames.ProbeStatus).DataPoints.Count,
                snapshot.Components.Count);
        }
    }

    [Theory]
    [InlineData(321)]
    [InlineData(322)]
    public void WebhookUrlRedactionNeverEchoesTheSecretPart(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        string[] schemes = ["https", "http", "ftp", "file", "HTTPS", "javascript", string.Empty, "mailto", "ws"];
        string[] hosts = ["hooks.example.com", "a.example.com", "example.com", "[::1]", "127.0.0.1", "10.0.0.1:8443", "EXAMPLE.com", "hooks.example.com.", "bücher.example", "xn--bcher-kva.example", "hooks.example.com:443", "hooks.example.com:80", "", "a b.example"];
        string[] users = [string.Empty, "user:SECRETPASS@", "SECRETUSER@", ":SECRETPASS@"];
        KodoWebhookAllowList list = new(["hooks.example.com", "*.example.com"]);

        for (int iteration = 0; iteration < 800; iteration++)
        {
            string scheme = Pick(random, schemes);
            string url = $"{scheme}://{Pick(random, users)}{Pick(random, hosts)}/hook/SECRETPATH{random.Next(3) switch { 0 => "?token=SECRETQUERY", 1 => "#SECRETFRAG", _ => string.Empty }}";
            if (random.Next(8) == 0)
            {
                url = Encoding.Latin1.GetString(FuzzKit.RandomBytes(random, 40));
            }

            string redacted = KodoWebhookUrl.Redact(url);
            Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
            Assert.True(
                redacted == KodoWebhookUrl.UnparsedText || redacted.EndsWith(KodoWebhookUrl.RedactedPath, StringComparison.Ordinal),
                $"seed={seed} iteration={iteration}: '{redacted}'");

            KodoWebhookDestinationDecision decision = list.Evaluate(url);
            Assert.Equal(redacted, decision.RedactedUrl);
            Assert.DoesNotContain("SECRET", decision.Reason, StringComparison.Ordinal);
            if (decision.Allowed)
            {
                Uri uri = new(url);
                Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
                Assert.Equal(443, uri.Port);
                Assert.Equal(string.Empty, uri.UserInfo);
                Assert.NotEqual(UriHostNameType.IPv4, uri.HostNameType);
                Assert.NotEqual(UriHostNameType.IPv6, uri.HostNameType);
                Assert.True(
                    uri.Host.Equals("hooks.example.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase),
                    $"seed={seed} iteration={iteration}: allowed host '{uri.Host}'");
            }
        }
    }

    [Theory]
    [InlineData(331)]
    [InlineData(332)]
    public void AllowListEntriesAreRefusedWithArgumentExceptionOnly(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        string[] pieces = ["a", "example", "com", "*.", "*", ".", "-", "..", "1.2.3.4", "::1", "http://", ":443", "/x", " ", "\n", "é", "xn--a", "A", string.Empty];
        for (int iteration = 0; iteration < 800; iteration++)
        {
            string entry = string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => Pick(random, pieces)));
            try
            {
                KodoWebhookAllowList list = new([entry]);
                // An accepted entry must allow only https names, never an address.
                Assert.False(list.Evaluate("https://127.0.0.1/x").Allowed, $"seed={seed} entry='{entry}'");
                Assert.False(list.Evaluate("http://" + entry.Trim().TrimStart('*', '.') + "/x").Allowed, $"seed={seed} entry='{entry}'");
            }
            catch (ArgumentException)
            {
                // The documented refusal.
            }
        }
    }

    [Theory]
    [InlineData(341)]
    [InlineData(342)]
    public void EnvelopeBuilderRefusesOrWritesValidJsonWithStrictIdentifiers(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        string[] alphabet = ["a", "Z", "0", "_", "-", ".", ":", "\n", " ", "\"", "\\", "/", "é", "\0", "{", "}"];
        string RandomText() => string.Concat(Enumerable.Range(0, random.Next(0, 6)).Select(_ => Pick(random, alphabet)));

        for (int iteration = 0; iteration < 800; iteration++)
        {
            string eventType = random.Next(3) == 0 ? RandomText() : Pick(random, [.. KodoWebhookEvents.All]);
            string eventId = RandomText();
            Dictionary<string, string> identifiers = [];
            int count = random.Next(0, 4);
            for (int i = 0; i < count; i++)
            {
                identifiers[random.Next(2) == 0 ? "probe_id" : RandomText()] = RandomText();
            }

            KodoWebhookEnvelope envelope;
            try
            {
                envelope = KodoWebhookEnvelopeBuilder.Build(eventType, eventId, KodoApiTestData.Now, identifiers);
            }
            catch (ArgumentException)
            {
                continue;
            }

            string where = $"seed={seed} iteration={iteration}";
            Assert.True(StrictEventId().IsMatch(eventId), $"{where}: event id '{eventId}' accepted");
            using JsonDocument body = JsonDocument.Parse(envelope.Body);
            Assert.Equal(eventId, body.RootElement.GetProperty("id").GetString());
            foreach (JsonProperty property in body.RootElement.GetProperty("identifiers").EnumerateObject())
            {
                Assert.True(StrictName().IsMatch(property.Name), $"{where}: identifier name '{property.Name}' accepted");
                Assert.True(StrictIdentifierValue().IsMatch(property.Value.GetString()!), $"{where}: identifier value '{property.Value}' accepted");
            }
        }
    }

    [Theory]
    [InlineData(351)]
    [InlineData(352)]
    public void SigningKeysAreRefusedWithArgumentExceptionOrHaveAHeaderSafeId(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        string[] alphabet = ["k", "1", "_", "-", "\n", "\r", " ", ":", "é", "\0"];
        for (int iteration = 0; iteration < 800; iteration++)
        {
            string keyId = string.Concat(Enumerable.Range(0, random.Next(0, 5)).Select(_ => Pick(random, alphabet)));
            byte[] secret = FuzzKit.RandomBytes(random, 40);
            try
            {
                KodoWebhookSigningKey key = new(keyId, secret);
                Assert.True(StrictKeyId().IsMatch(key.KeyId), $"seed={seed} iteration={iteration}: key id '{key.KeyId}' accepted");
                Assert.True(secret.Length >= KodoWebhookSigningKey.MinimumSecretLength);
                Assert.DoesNotContain(Convert.ToHexString(secret), key.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                // The documented refusal.
            }
        }
    }

    [Theory]
    [InlineData(361)]
    [InlineData(362)]
    [InlineData(363)]
    public void RetryPlannerKeepsItsBoundsForAnyElapsedTimeAndAttemptCount(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        TimeSpan[] elapsedValues = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromHours(23), TimeSpan.FromHours(24), TimeSpan.FromDays(400), TimeSpan.MaxValue];
        int[] attempts = [1, 2, 3, 10, 100, 1000, int.MaxValue];

        for (int iteration = 0; iteration < 600; iteration++)
        {
            double jitter = random.NextDouble() * 0.99;
            TimeSpan initial = TimeSpan.FromSeconds(random.Next(1, 600));
            TimeSpan max = initial + TimeSpan.FromSeconds(random.Next(0, 20000));
            TimeSpan window = max + TimeSpan.FromSeconds(random.Next(0, 200000));
            KodoWebhookRetryPolicy policy = new(initial, 1 + (random.NextDouble() * 4), max, jitter, window);
            string where = $"seed={seed} iteration={iteration}";

            int failed = attempts[random.Next(attempts.Length)];
            TimeSpan elapsed = elapsedValues[random.Next(elapsedValues.Length)];
            KodoWebhookDecision decision = policy.Decide(KodoWebhookOutcome.RetryableFailure, failed, elapsed, random);

            TimeSpan nominal = policy.NominalDelay(failed);
            Assert.InRange(nominal, initial, max);
            if (decision.Step == KodoWebhookStep.Retry)
            {
                Assert.True(elapsed + decision.Delay <= window, $"{where}: retry lands after the window");
                Assert.InRange(decision.Delay.TotalSeconds, (nominal.TotalSeconds * (1 - jitter)) - 1e-6, nominal.TotalSeconds + 1e-6);
            }
            else
            {
                Assert.Equal(KodoWebhookStep.DeadLetter, decision.Step);
                Assert.Equal(TimeSpan.Zero, decision.Delay);
                Assert.Equal("schedule_exhausted", decision.Reason);
            }

            Assert.Equal(KodoWebhookStep.Done, policy.Decide(KodoWebhookOutcome.Delivered, failed, elapsed, random).Step);
            Assert.Equal(KodoWebhookStep.DeadLetter, policy.Decide(KodoWebhookOutcome.PermanentFailure, failed, elapsed, random).Step);
        }
    }

    [Theory]
    [InlineData(371)]
    [InlineData(372)]
    public void RetryPlanEndsWithinTheWindowAndNominalDelaysNeverShrink(int seed)
    {
        Random random = FuzzKit.Rng(seed);
        for (int iteration = 0; iteration < 150; iteration++)
        {
            double jitter = random.NextDouble() * 0.99;
            TimeSpan initial = TimeSpan.FromSeconds(random.Next(30, 600));
            TimeSpan max = initial + TimeSpan.FromSeconds(random.Next(0, 20000));
            KodoWebhookRetryPolicy policy = new(initial, 1 + (random.NextDouble() * 3), max, jitter, TimeSpan.FromHours(24) + max);
            string where = $"seed={seed} iteration={iteration}";

            IReadOnlyList<TimeSpan> delays = policy.PlanDelays(random);

            Assert.True(delays.Count > 0, where);
            Assert.True(delays.Count <= 24 * 3600 / 30, $"{where}: too many retries");
            Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay) <= policy.Window, $"{where}: plan exceeds the window");
            for (int i = 0; i < delays.Count; i++)
            {
                Assert.True(delays[i] <= policy.NominalDelay(i + 1) && delays[i] > TimeSpan.Zero, $"{where}: delay {i}");
            }

            for (int attempt = 1; attempt < 60; attempt++)
            {
                Assert.True(policy.NominalDelay(attempt) <= policy.NominalDelay(attempt + 1), $"{where}: nominal delay shrank at {attempt}");
            }
        }
    }

    [Fact]
    public void EveryHttpStatusMapsToOneOutcomeAndOthersAreRefused()
    {
        for (int status = 100; status <= 599; status++)
        {
            KodoWebhookOutcome outcome = KodoWebhookOutcomes.FromStatusCode(status);
            bool success = status is >= 200 and <= 299;
            Assert.Equal(success, outcome == KodoWebhookOutcome.Delivered);
            Assert.Equal(status is 408 or 429 or (>= 500 and <= 599), outcome == KodoWebhookOutcome.RetryableFailure);
        }

        foreach (int status in new[] { int.MinValue, -1, 0, 99, 600, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => KodoWebhookOutcomes.FromStatusCode(status));
        }
    }

    [Theory]
    [InlineData("k1\n")]
    [InlineData("\n")]
    public void AnIdentifierWithATrailingLineFeedIsRefusedEverywhere(string value)
    {
        // Regression: a pattern ending in $ lets one trailing LF through, which would reach a header (key id) or a label.
        Assert.Throws<ArgumentException>(() => new KodoWebhookSigningKey(value, new byte[KodoWebhookSigningKey.MinimumSecretLength]));
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(KodoWebhookEvents.Alert, value, KodoApiTestData.Now, new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(KodoWebhookEvents.Alert, "id1", KodoApiTestData.Now, new Dictionary<string, string> { ["probe_id"] = "a" + value }));
        Assert.Throws<ArgumentException>(() => KodoWebhookEnvelopeBuilder.Build(KodoWebhookEvents.Alert, "id1", KodoApiTestData.Now, new Dictionary<string, string> { ["probe_id\n"] = "a" }));
    }

    [Theory]
    [InlineData(2262, 4, 12)]
    [InlineData(3000, 1, 1)]
    [InlineData(9999, 12, 31)]
    [InlineData(1677, 9, 20)]
    [InlineData(1, 1, 1)]
    public void OtlpMapperHoldsAFarTimeAtTheEdgeInsteadOfOverflowing(int year, int month, int day)
    {
        DateTimeOffset far = new(year, month, day, 0, 0, 0, TimeSpan.Zero);
        KodoStatusSnapshotDto snapshot = SnapshotAt(far);

        IReadOnlyList<KodoOtlpGauge> gauges = KodoOtlpMapper.Map(snapshot);

        long nano = gauges[0].DataPoints[0].TimeUnixNano;
        Assert.Equal(far < DateTimeOffset.UnixEpoch, nano < 0);
        Assert.All(gauges.SelectMany(gauge => gauge.DataPoints), point => Assert.Equal(nano, point.TimeUnixNano));
    }

    [Fact]
    public void RetryPlannerDeadLettersAnElapsedTimeThatWouldOverflowTheSum()
    {
        // Regression: elapsed + delay overflowed TimeSpan for an elapsed time near TimeSpan.MaxValue.
        KodoWebhookDecision decision = KodoWebhookRetryPolicy.Defaults.Decide(
            KodoWebhookOutcome.RetryableFailure, 1, TimeSpan.MaxValue, FuzzKit.Rng(1));

        Assert.Equal(KodoWebhookStep.DeadLetter, decision.Step);
        Assert.Equal("schedule_exhausted", decision.Reason);
    }

    private static KodoStatusSnapshotDto SnapshotAt(DateTimeOffset asOf)
    {
        KodoStatusSnapshotDto baseline = KodoApiTestData.Snapshot(1);
        return baseline with { Status = baseline.Status with { AsOf = asOf } };
    }
}
