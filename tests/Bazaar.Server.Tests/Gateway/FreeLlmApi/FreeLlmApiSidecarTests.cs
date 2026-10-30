using System.Text.Json;
using Bazaar.Server.Gateway.FreeLlmApi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// The managed FreeLLMAPI sidecar's settings, start plan and private state files. Process start and the HTTP provisioning
/// are exercised against a real FreeLLMAPI build by the host; these tests cover the parts that can run without Node.
/// </summary>
public sealed class FreeLlmApiSidecarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-sidecar-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FreeLlmApiSidecarOptions Managed() => new()
    {
        Managed = true,
        StateDirectory = _root,
        InstallDirectory = Path.Combine(_root, "app"),
    };

    [Fact]
    public void Managed_defaults_point_the_gateway_at_loopback_and_the_unified_key_file()
    {
        var options = new FreeLlmApiOptions { Sidecar = Managed() };

        options.ApplyManagedSidecarDefaults();

        Assert.True(options.Enabled);
        Assert.Equal("http://127.0.0.1:3001/v1", options.BaseUrl);
        Assert.Equal("file:" + Path.Combine(_root, "unified-key"), options.ApiKey);
        // DefaultModel is still the placeholder "auto", which the validator refuses. The model-policy work sets the concrete id.
    }

    [Fact]
    public void An_explicit_base_url_is_kept_but_the_sidecar_always_owns_the_key()
    {
        var options = new FreeLlmApiOptions
        {
            Sidecar = Managed(),
            BaseUrl = "http://127.0.0.1:4000/v1",
            ApiKey = "env:OPERATOR_KEY",
        };

        options.ApplyManagedSidecarDefaults();

        Assert.Equal("http://127.0.0.1:4000/v1", options.BaseUrl);
        Assert.Equal("file:" + Path.Combine(_root, "unified-key"), options.ApiKey);
    }

    [Fact]
    public void A_placeholder_or_empty_default_model_becomes_a_concrete_managed_model()
    {
        var auto = new FreeLlmApiOptions { Sidecar = Managed(), DefaultModel = "auto" };
        auto.ApplyManagedSidecarDefaults();
        Assert.Equal(FreeLlmApiOptions.ManagedDefaultModel, auto.DefaultModel);

        var configured = new FreeLlmApiOptions { Sidecar = Managed(), DefaultModel = "mistral/mistral-small" };
        configured.ApplyManagedSidecarDefaults();
        Assert.Equal("mistral/mistral-small", configured.DefaultModel);
    }

    [Fact]
    public void An_unmanaged_sidecar_changes_nothing()
    {
        var options = new FreeLlmApiOptions();

        options.ApplyManagedSidecarDefaults();

        Assert.False(options.Enabled);
        Assert.Equal("", options.ApiKey);
    }

    [Fact]
    public void The_validator_names_each_bad_sidecar_setting()
    {
        var bad = new FreeLlmApiSidecarOptions { Managed = true, NodeExecutable = " ", Port = 80, StartupTimeoutSeconds = 1 };

        var result = new FreeLlmApiSidecarOptionsValidator().Validate(null, bad);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("NodeExecutable", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("Port", StringComparison.Ordinal));
        Assert.Contains(result.Failures, f => f.Contains("StartupTimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void The_start_plan_binds_loopback_and_passes_the_generated_key_and_config_path()
    {
        var options = Managed();
        var plan = FreeLlmApiSidecarPlan.Create(options, new string('a', 64), Path.Combine(_root, "declarative-config.json"));

        Assert.Equal("node", plan.Executable);
        Assert.EndsWith(Path.Combine("server", "dist", "index.js"), plan.Arguments[0], StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", plan.Environment["HOST"]);
        Assert.Equal("3001", plan.Environment["PORT"]);
        Assert.Equal("production", plan.Environment["NODE_ENV"]);
        Assert.Equal(new string('a', 64), plan.Environment["ENCRYPTION_KEY"]);
        Assert.Equal(Path.Combine(_root, "declarative-config.json"), plan.Environment["FREEAPI_CONFIG_PATH"]);
        Assert.Equal(Path.Combine(_root, "freellmapi.db"), plan.DatabasePath);
    }

    [Fact]
    public void The_encryption_key_is_created_once_and_reused()
    {
        var first = FreeLlmApiSidecarState.EnsureEncryptionKey(_root);
        var second = FreeLlmApiSidecarState.EnsureEncryptionKey(_root);

        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void The_declarative_config_carries_the_admin_until_the_account_is_marked_created()
    {
        var keys = new[] { new ProviderKeyEntry("groq", "gsk_test_value", "free") };

        var before = JsonDocument.Parse(File.ReadAllText(FreeLlmApiSidecarState.WriteDeclarativeConfig(_root, keys)));
        Assert.True(before.RootElement.TryGetProperty("admin", out _));
        Assert.Equal("groq", before.RootElement.GetProperty("keys")[0].GetProperty("platform").GetString());

        FreeLlmApiSidecarState.MarkAdminCreated(_root);
        var after = JsonDocument.Parse(File.ReadAllText(FreeLlmApiSidecarState.WriteDeclarativeConfig(_root, keys)));
        Assert.False(after.RootElement.TryGetProperty("admin", out _));
    }

    [Fact]
    public void Provider_keys_are_optional_and_entries_without_a_key_are_refused_without_echoing_values()
    {
        Assert.Empty(FreeLlmApiSidecarState.ReadProviderKeys(Path.Combine(_root, "missing.json")));

        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "provider-keys.json");
        File.WriteAllText(path, "[{\"platform\":\"groq\",\"key\":\"\"}]");

        var error = Assert.Throws<InvalidDataException>(() => FreeLlmApiSidecarState.ReadProviderKeys(path));
        Assert.Contains("without a platform or key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_keys_parse_from_camel_case_json()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "provider-keys.json");
        File.WriteAllText(path, "[{\"platform\":\"cerebras\",\"key\":\"csk_x\",\"label\":\"free\"}]");

        var entries = FreeLlmApiSidecarState.ReadProviderKeys(path);

        Assert.Single(entries);
        Assert.Equal("cerebras", entries[0].Platform);
        Assert.Equal("csk_x", entries[0].Key);
    }
}
