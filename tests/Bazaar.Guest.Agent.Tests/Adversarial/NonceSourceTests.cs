using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests.Adversarial;

/// <summary>The Hello nonce must not travel on a command line that every process in the guest can read, and must never reach an exec's child.</summary>
public sealed class NonceSourceTests
{
    private const string _name = GuestAgentOptions.NonceEnvironmentVariable;

    private static GuestAgentOptions Parse(FakeNonceEnvironment env, StringWriter? log, params string[] extra)
    {
        string[] common = ["--vm-id", "vm-1", "--root", Path.GetTempPath()];
        return GuestAgentOptions.Parse([.. common, .. extra], env, log ?? new StringWriter());
    }

    [Fact]
    public async Task A_nonce_file_is_read_trimmed_of_one_trailing_newline_and_deleted()
    {
        await using var dir = new TempRoot();
        var file = Path.Combine(dir.Root, "nonce");
        await File.WriteAllTextAsync(file, "s3cret\n", TestContext.Current.CancellationToken);

        var options = Parse(new FakeNonceEnvironment(), null, "--nonce-file", file);

        Assert.Equal("s3cret", options.Nonce);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("s3cret\r\n", "s3cret")]
    [InlineData("s3cret", "s3cret")]
    [InlineData("s3cret\n\n", "s3cret\n")]
    [InlineData("  s3cret  \n", "  s3cret  ")]
    public async Task Only_one_trailing_newline_is_trimmed(string content, string expected)
    {
        await using var dir = new TempRoot();
        var file = Path.Combine(dir.Root, "nonce");
        await File.WriteAllTextAsync(file, content, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Parse(new FakeNonceEnvironment(), null, "--nonce-file", file).Nonce);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task An_empty_nonce_file_is_refused(string content)
    {
        await using var dir = new TempRoot();
        var file = Path.Combine(dir.Root, "nonce");
        await File.WriteAllTextAsync(file, content, TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentException>(() => Parse(new FakeNonceEnvironment(), null, "--nonce-file", file));
    }

    [Fact]
    public async Task A_missing_nonce_file_is_refused()
    {
        await using var dir = new TempRoot();

        Assert.Throws<ArgumentException>(() => Parse(new FakeNonceEnvironment(), null, "--nonce-file", Path.Combine(dir.Root, "absent")));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_nonce_file_that_cannot_be_deleted_is_only_logged()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Uses POSIX permissions.");
        Assert.SkipWhen(Environment.UserName == "root", "Root ignores directory permissions.");
        await using var dir = new TempRoot();
        var sub = Path.Combine(dir.Root, "ro");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "nonce");
        await File.WriteAllTextAsync(file, "keepme\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var log = new StringWriter();

            var options = Parse(new FakeNonceEnvironment(), log, "--nonce-file", file);

            Assert.Equal("keepme", options.Nonce);
            Assert.Contains("could not delete", log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_environment_variable_is_used_and_removed()
    {
        var env = new FakeNonceEnvironment { Value = "from-env" };

        var options = Parse(env, null);

        Assert.Equal("from-env", options.Nonce);
        Assert.Null(env.Value);
    }

    [Fact]
    public void An_empty_environment_variable_counts_as_unset_and_is_still_removed()
    {
        var env = new FakeNonceEnvironment { Value = "" };

        var options = Parse(env, null, "--nonce", "from-arg");

        Assert.Equal("from-arg", options.Nonce);
        Assert.Null(env.Value);
    }

    [Fact]
    public void The_command_line_nonce_still_works_when_it_is_the_only_source()
    {
        Assert.Equal("from-arg", Parse(new FakeNonceEnvironment(), null, "--nonce", "from-arg").Nonce);
    }

    [Fact]
    public void No_nonce_at_all_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Parse(new FakeNonceEnvironment(), null));
        Assert.Throws<ArgumentException>(() => Parse(new FakeNonceEnvironment(), null, "--nonce", ""));
    }

    [Fact]
    public async Task More_than_one_source_is_refused_and_the_environment_is_still_cleared()
    {
        await using var dir = new TempRoot();
        var file = Path.Combine(dir.Root, "nonce");
        await File.WriteAllTextAsync(file, "f\n", TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentException>(() => Parse(new FakeNonceEnvironment(), null, "--nonce-file", file, "--nonce", "a"));
        var env = new FakeNonceEnvironment { Value = "e" };
        Assert.Throws<ArgumentException>(() => Parse(env, null, "--nonce", "a"));
        Assert.Null(env.Value);
        env.Value = "e";
        Assert.Throws<ArgumentException>(() => Parse(env, null, "--nonce-file", file));
        Assert.Null(env.Value);
        // A refused start must not have consumed the file either.
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void The_process_environment_variable_is_removed_after_it_is_read()
    {
        Environment.SetEnvironmentVariable(_name, "process-secret");
        try
        {
            var options = GuestAgentOptions.Parse(["--vm-id", "vm-1", "--root", Path.GetTempPath()]);

            Assert.Equal("process-secret", options.Nonce);
            Assert.Null(Environment.GetEnvironmentVariable(_name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(_name, null);
        }
    }

    [Theory]
    [InlineData("BAZAAR_GUEST_NONCE")]
    [InlineData("bazaar_guest_nonce")]
    public async Task An_exec_never_receives_the_nonce_even_when_the_allowlist_names_it(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux process tooling required.");
        const string secret = "must-not-leak-9f3a";
        Environment.SetEnvironmentVariable(_name, secret);
        try
        {
            await using var root = new TempRoot();
            await using var guest = new GuestHarness(root.Options());
            await guest.ReceiveAsync<Hello>();

            await guest.SendAsync(new Exec("e1", ["/bin/sh", "-c", "env"], root.Root, [name, "PATH"], 30));
            var result = await guest.CollectExecAsync("e1").WaitAsync(GuestHarness.Bound, TestContext.Current.CancellationToken);

            Assert.DoesNotContain(secret, result.Text(OutputSource.Stdout), StringComparison.Ordinal);
            Assert.DoesNotContain("BAZAAR_GUEST_NONCE", result.Text(OutputSource.Stdout), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_name, null);
        }
    }

    private sealed class FakeNonceEnvironment : INonceEnvironment
    {
        public string? Value { get; set; }

        public string? Take(string name)
        {
            var value = Value;
            Value = null;
            return value;
        }
    }
}
