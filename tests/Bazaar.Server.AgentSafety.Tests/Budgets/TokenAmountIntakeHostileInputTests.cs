using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Http;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Http;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// Hostile bodies for <c>POST /blobs/token-amounts</c> over raw sockets: bodies cut mid-chunk or short of their declared length,
/// slow and abandoned bodies, concurrent intakes and guard edge values. Each ends in a designed status or a closed connection,
/// never a 500, and never leaves a staged or sealed amount behind.
/// </summary>
public sealed class TokenAmountIntakeHostileInputTests : IAsyncDisposable
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), "bazaar-intake-hostile-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private readonly List<string> _errors = [];
    private int _started;
    private int _finished;

    public ValueTask DisposeAsync()
    {
        // The invariant of the whole class: no unhandled exception, whatever the client sent.
        Assert.Empty(_errors);
        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, int Port)> StartAsync(NpgsqlDataSource dataSource, TokenAmountBounds guard)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _blobRoot });
        builder.Logging.AddProvider(new ErrorSink(_errors));
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new Credentials());
        builder.Services.AddSingleton(new PostgresLargeTokenAmountStore(dataSource));
        builder.Services.AddSingleton(new TokenBudgetOptions { AmountIntakeMaxDigits = guard });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            Interlocked.Increment(ref _started);
            try
            {
                await next();
            }
            finally
            {
                Interlocked.Increment(ref _finished);
            }
        });
        app.MapBazaarTokenAmountIntake();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new Uri(address).Port);
    }

    private static string Huge(int digits) => "1" + new string('0', digits - 1);

    private static string Head(string extra, string length) =>
        "POST /blobs/token-amounts HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer owner\r\n" + extra + length + "\r\nConnection: close\r\n\r\n";

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>
    /// Sends the parts with a pause between them, then half-closes and reads the status code the server answers (0 for a socket
    /// closed without an answer). With <paramref name="abandon"/> the connection is reset after the last part instead.
    /// </summary>
    private static async Task<int> SendAsync(int port, IEnumerable<byte[]> parts, bool abandon = false, int pauseMs = 0, bool halfClose = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        var stream = client.GetStream();
        foreach (var part in parts)
        {
            try
            {
                await stream.WriteAsync(part, Ct);
            }
            catch (IOException)
            {
                // The server cut the request off and closed: read whatever answer it left.
                break;
            }

            if (pauseMs > 0)
            {
                await Task.Delay(pauseMs, Ct);
            }
        }

        if (abandon)
        {
            client.Client.LingerState = new LingerOption(true, 0);
            return 0;
        }

        if (halfClose)
        {
            // Kestrel reads a closed send side as an abort, so only the cut-body cases do this.
            client.Client.Shutdown(SocketShutdown.Send);
        }

        var buffer = new byte[256];
        var read = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (read < 12)
        {
            int got;
            try
            {
                got = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            }
            catch (IOException)
            {
                break;
            }

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        return text.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) ? int.Parse(text.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Cleanup of an abandoned intake runs after the socket closed, so give it a moment before counting.</summary>
    private async Task<(long, long, long)> SettledCountsAsync(NpgsqlDataSource dataSource)
    {
        // A closed socket may be answered before the server has even started the request: wait for it to run to its end.
        for (var attempt = 0; attempt < 200 && (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _started) != Volatile.Read(ref _finished)); attempt++)
        {
            await Task.Delay(50, Ct);
        }

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var counts = await LargeTierTestSupport.CountsAsync(dataSource, Ct);
            if (counts == (0L, 0L, 0L))
            {
                return counts;
            }

            await Task.Delay(100, Ct);
        }

        return await LargeTierTestSupport.CountsAsync(dataSource, Ct);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(".5")]
    [InlineData("5.")]
    [InlineData("1.2.3")]
    [InlineData("+5")]
    [InlineData("-5")]
    [InlineData("1e5")]
    [InlineData(" 5")]
    [InlineData("5\n")]
    [InlineData("5\r\n")]
    [InlineData("5\0")]
    [InlineData("\0")]
    [InlineData("٣")]
    public async Task Malformed_texts_are_a_400_and_store_nothing(string body)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var raw = Encoding.UTF8.GetBytes(body);
            var status = await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + raw.Length)), raw]);

            Assert.Equal(400, status);
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00000000000000000000000000000")]
    [InlineData("0.0")]
    [InlineData("007")]
    [InlineData("12345.6789")]
    public async Task Zero_and_inline_sized_amounts_are_a_422_even_with_leading_zeros(string body)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var status = await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + body.Length)), Bytes(body)]);

            Assert.Equal(422, status);
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Fact]
    public async Task Leading_zeros_count_against_the_guard_but_are_not_stored()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, new TokenAmountBounds(300_000, 10));
        await using (app)
        {
            var fits = new string('0', 100_000) + Huge(200_000);
            Assert.Equal(201, await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + fits.Length)), Bytes(fits)]));

            var over = new string('0', 100_001) + Huge(200_000);
            Assert.Equal(413, await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + over.Length)), Bytes(over)]));
        }
    }

    [Theory]
    [InlineData(250_000, 100, 201)]
    [InlineData(250_001, 100, 413)]
    [InlineData(250_000, 101, 413)]
    public async Task Guard_edge_values_are_exact(int integerDigits, int fractionDigits, int expected)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, new TokenAmountBounds(250_000, 100));
        await using (app)
        {
            var body = Huge(integerDigits) + "." + new string('7', fractionDigits);
            var status = await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + body.Length)), Bytes(body)]);

            Assert.Equal(expected, status);
        }
    }

    [Fact]
    public async Task A_body_shorter_than_its_content_length_is_never_a_500_and_stages_nothing()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var body = Huge(300_000);
            var status = await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + (body.Length + 5000))), Bytes(body)], halfClose: true);

            Assert.True(status is 0 or (>= 400 and < 500), $"status {status}");
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Fact]
    public async Task A_chunked_body_cut_in_the_middle_of_a_chunk_stages_nothing()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var body = Huge(300_000);

            // The chunk announces 0x493E0 (300000) bytes, the client sends 1000 of them and half-closes.
            var status = await SendAsync(port, [Bytes(Head("Transfer-Encoding: chunked\r\n", string.Empty)), Bytes("493E0\r\n"), Bytes(body[..1000])], halfClose: true);

            Assert.True(status is 0 or (>= 400 and < 500), $"status {status}");
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Theory]
    [InlineData("ZZ\r\n")]
    [InlineData("-5\r\nabcde\r\n0\r\n\r\n")]
    [InlineData("5\r\n12345XX0\r\n\r\n")]
    [InlineData("FFFFFFFFFFFFFFFFFF\r\n1")]
    public async Task A_garbled_chunk_framing_is_a_designed_4xx_or_a_closed_socket(string framing)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var status = await SendAsync(port, [Bytes(Head("Transfer-Encoding: chunked\r\n", string.Empty)), Bytes(framing)], halfClose: true);

            Assert.True(status is 0 or (>= 400 and < 500), $"status {status}");
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Fact]
    public async Task A_client_that_vanishes_mid_body_leaves_no_staged_rows()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var body = Huge(400_000);
            await SendAsync(port, [Bytes(Head(string.Empty, "Content-Length: " + body.Length)), Bytes(body[..250_000])], abandon: true);

            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Fact]
    public async Task A_body_trickled_below_the_minimum_data_rate_is_cut_off_and_stages_nothing()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            // One byte a second: Kestrel's minimum body data rate ends the request after its grace period.
            var parts = new List<byte[]> { Bytes(Head(string.Empty, "Content-Length: 400000")) };
            parts.AddRange(Enumerable.Repeat(Bytes("1"), 9));
            var status = await SendAsync(port, parts, pauseMs: 1000);

            Assert.True(status is 0 or (>= 400 and < 500), $"status {status}");
            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    [Fact]
    public async Task Two_slow_intakes_run_side_by_side_and_both_seal()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var body = Huge(200_001);
            List<byte[]> Trickle()
            {
                var parts = new List<byte[]> { Bytes(Head(string.Empty, "Content-Length: " + body.Length)) };
                for (var at = 0; at < body.Length; at += 50_000)
                {
                    parts.Add(Bytes(body[at..Math.Min(body.Length, at + 50_000)]));
                }

                return parts;
            }

            var statuses = await Task.WhenAll(SendAsync(port, Trickle(), pauseMs: 150), SendAsync(port, Trickle(), pauseMs: 100));

            Assert.Equal([201, 201], statuses);
            var (_, headers, _) = await LargeTierTestSupport.CountsAsync(db.DataSource, Ct);
            Assert.Equal(2L, headers);
        }
    }

    [Fact]
    public async Task Duplicate_authorization_headers_the_wrong_method_and_nul_bytes_are_refused_before_any_body_is_read()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, port) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var body = Huge(200_001);
            var doubled = "POST /blobs/token-amounts HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer nobody\r\nAuthorization: Bearer owner\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            Assert.Equal(401, await SendAsync(port, [Bytes(doubled), Bytes(body)]));

            var get = "GET /blobs/token-amounts HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer owner\r\nConnection: close\r\n\r\n";
            Assert.Equal(405, await SendAsync(port, [Bytes(get)]));

            var nul = "POST /blobs/token-amounts HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer ow\0ner\r\nContent-Length: 1\r\nConnection: close\r\n\r\n1";
            Assert.True(await SendAsync(port, [Bytes(nul)]) is 0 or 400 or 401);

            Assert.Equal((0L, 0L, 0L), await SettledCountsAsync(db.DataSource));
        }
    }

    private sealed class ErrorSink(List<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(errors, categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(List<string> errors, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    lock (errors)
                    {
                        errors.Add(category + ": " + exception?.GetType().Name + " " + formatter(state, exception));
                    }
                }
            }
        }
    }

    private sealed class Credentials : IConnectionAuthenticator
    {
        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential?.Value == "owner" ? AuthResult.Success(new PrincipalRef(_owner, "owner")) : AuthResult.Fail("unknown credential"));
    }
}
