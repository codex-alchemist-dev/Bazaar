namespace Bazaar.Server.Valkey.Tests;

/// <summary>The managed child's connection: generated password, ready gating and redaction.</summary>
public class ValkeyManagedConnectionTests
{
    [Fact]
    public async Task No_connection_is_offered_until_the_child_is_ready()
    {
        using var connection = new ValkeyManagedConnection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.GetConnectionStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The managed Valkey server is not running.", ex.Message);
    }

    [Fact]
    public async Task Ready_connection_carries_the_port_and_the_generated_password()
    {
        using var connection = new ValkeyManagedConnection();
        connection.Prepare(41234);
        connection.MarkReady();

        var text = await connection.GetConnectionStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"127.0.0.1:41234,password={connection.PasswordForChildConfig()}", text);
        Assert.Equal(41234, connection.Port);
    }

    [Fact]
    public async Task Connection_is_withdrawn_when_the_child_is_marked_unavailable()
    {
        using var connection = new ValkeyManagedConnection();
        connection.Prepare(41234);
        connection.MarkReady();
        connection.MarkUnavailable();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connection.GetConnectionStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Generated_password_is_256_bits_of_hex_and_differs_per_host_run()
    {
        using var first = new ValkeyManagedConnection();
        using var second = new ValkeyManagedConnection();

        var password = first.PasswordForChildConfig();

        Assert.Equal(64, password.Length);
        Assert.Matches("^[0-9A-F]{64}$", password);
        Assert.NotEqual(password, second.PasswordForChildConfig());
    }

    [Fact]
    public void Connection_text_never_shows_the_password_in_its_string_form()
    {
        using var connection = new ValkeyManagedConnection();

        Assert.DoesNotContain(connection.PasswordForChildConfig(), connection.ToString(), StringComparison.Ordinal);
    }
}
