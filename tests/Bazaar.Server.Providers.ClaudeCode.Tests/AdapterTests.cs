using System.Diagnostics;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

public sealed class AdapterTests
{
    private const string _mcp = "#mcp";

    [Fact]
    public async Task Happy_path_streams_text_usage_and_done()
    {
        using var h = new Harness();
        h.Play(Lines.HappyPath);
        var events = await h.RunAsync();
        Assert.Equal(["Hello", " world"], events.OfType<ModelEvent.TextDelta>().Select(e => e.Text));
        Assert.Equal(new GatewayTokenUsage(12, 0, 0, 3, 5, 0), events.OfType<ModelEvent.UsageReported>().Single().Usage);
        var done = Assert.IsType<ModelEvent.Done>(events[^1]);
        Assert.Equal(StopReason.EndTurn, done.StopReason);
        Assert.Equal("claude-haiku-5-5", done.ServedModel);
        Assert.True(h.State.Current.Up);
        Assert.NotNull(h.State.Current.LastSuccessAt);
    }

    [Fact]
    public async Task Bazaars_system_prompt_replaces_the_default_one_exactly()
    {
        using var h = new Harness();
        h.Play(Lines.HappyPath);
        var system = "You are Ada, a staff engineer at Codex Alchemist.\nSoul: " + new string('s', 50_000) + "\nEND-OF-SOUL";
        await h.RunAsync([new ModelMessage(ModelRole.System, system), new ModelMessage(ModelRole.User, "hi")]);
        var dump = h.ReadDump();
        var args = dump.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Contains("--system-prompt-file", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--append-system-prompt", StringComparison.Ordinal));
        Assert.DoesNotContain("--system-prompt", args);
        Assert.Equal(system, dump.GetProperty("systemPrompt").GetString());
    }

    [Fact]
    public async Task Full_context_and_retrieval_results_reach_the_model_untruncated()
    {
        using var h = new Harness();
        h.Play(Lines.HappyPath);
        var rag = "[graphDRAG begin] " + new string('a', 150_000) + " MIDDLE-FACT " + new string('b', 150_000) + " [graphDRAG end]";
        await h.RunAsync(
        [
            new ModelMessage(ModelRole.System, "sys"),
            new ModelMessage(ModelRole.User, "Earlier question"),
            new ModelMessage(ModelRole.Assistant, "Earlier answer"),
            new ModelMessage(ModelRole.User, "Context:\n" + rag + "\nNow answer."),
        ]);
        var stdin = h.ReadDump().GetProperty("stdin").GetString()!;
        Assert.Contains(rag, stdin, StringComparison.Ordinal);
        Assert.Contains("Earlier question", stdin, StringComparison.Ordinal);
        Assert.Contains("Earlier answer", stdin, StringComparison.Ordinal);
        Assert.Contains("Now answer.", stdin, StringComparison.Ordinal);
        Assert.True(stdin.Length > 300_000);
    }

    [Fact]
    public async Task Only_Bazaars_tools_are_offered_and_the_bridge_never_runs_them()
    {
        using var h = new Harness();
        h.Play(Lines.Init, _mcp, Lines.MessageStart, Lines.ToolStart, Lines.ToolArgs, Lines.ToolStop, Lines.ToolUseEnd, Lines.MessageStop, "#sleep 30000");
        var sw = Stopwatch.StartNew();
        var events = await h.RunAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "the process must be stopped after the tool-use message, not waited for");

        var dump = h.ReadDump();
        var args = dump.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.DoesNotContain(args, a => a.Contains("allowedTools", StringComparison.OrdinalIgnoreCase) || a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));

        var results = dump.GetProperty("mcpResults").EnumerateArray().Select(r => r.GetString()!).ToList();
        using var list = JsonDocument.Parse(results[1]);
        var names = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["echo"], names);
        using var call = JsonDocument.Parse(results[2]);
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("401", results[3]);

        Assert.Equal("echo", events.OfType<ModelEvent.ToolCallStart>().Single().Name);
        Assert.Equal(StopReason.ToolUse, events.OfType<ModelEvent.Done>().Single().StopReason);
        Assert.Equal(9, events.OfType<ModelEvent.UsageReported>().Single().Usage.Output);
    }

    [Fact]
    public async Task The_bridge_token_is_not_on_the_command_line_and_the_config_file_is_gone_afterwards()
    {
        using var h = new Harness();
        h.Play(Lines.Init, _mcp, Lines.MessageStart, Lines.TextStart, Lines.Text1, Lines.BlockStop0, Lines.EndTurn, Lines.MessageStop, Lines.ResultOk);
        await h.RunAsync();
        var dump = h.ReadDump();
        var token = JsonElement.Parse(dump.GetProperty("mcpConfig").GetString()!).GetProperty("mcpServers").GetProperty("bazaar").GetProperty("headers").GetProperty("Authorization").GetString()!["Bearer ".Length..];
        Assert.DoesNotContain(dump.GetProperty("args").EnumerateArray(), a => a.GetString()!.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(token, dump.GetProperty("stdin").GetString()!, StringComparison.Ordinal);
        var mcpPath = dump.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).SkipWhile(a => a != "--mcp-config").Skip(1).First();
        Assert.False(File.Exists(mcpPath));
    }

    [Fact]
    public async Task Built_in_tools_in_init_fail_closed_and_take_the_lane_down()
    {
        using var h = new Harness();
        h.Play(Lines.InitWithBuiltIn, Lines.MessageStart);
        var events = await h.RunAsync();
        Assert.Equal("provider_error", Assert.IsType<ModelEvent.Failure>(events[^1]).Code);
        Assert.False(h.State.Current.Up);
        var again = await h.RunAsync();
        Assert.Contains("lane is down", Assert.IsType<ModelEvent.Failure>(again.Single()).Message, StringComparison.Ordinal);
        Assert.Equal(1, h.Runner.Starts);
    }

    [Fact]
    public async Task A_usage_limit_is_rate_limited_and_the_next_call_never_starts_a_process()
    {
        using var h = new Harness();
        h.Play(Lines.Init, """{"type":"result","subtype":"success","is_error":true,"result":"You have hit your usage limit. It resets in 2 hours."}""");
        var events = await h.RunAsync();
        var f = Assert.IsType<ModelEvent.Failure>(events[^1]);
        Assert.Equal("rate_limited", f.Code);
        Assert.Equal(7200, f.RetryAfterSeconds);
        Assert.True(h.State.Current.Up);
        Assert.NotNull(h.State.Current.RateLimitedUntil);

        var second = await h.RunAsync();
        Assert.Equal("rate_limited", Assert.IsType<ModelEvent.Failure>(second.Single()).Code);
        Assert.Equal(1, h.Runner.Starts);

        h.Time.Advance(TimeSpan.FromHours(3));
        h.Play(Lines.HappyPath);
        Assert.IsType<ModelEvent.Done>((await h.RunAsync())[^1]);
    }

    [Fact]
    public async Task Rate_limit_text_on_stderr_with_a_non_zero_exit_is_detected()
    {
        using var h = new Harness();
        h.Play(Lines.Init, "#stderr Claude usage limit reached. Resets in 30 minutes.", "#exit 1");
        var f = Assert.IsType<ModelEvent.Failure>((await h.RunAsync())[^1]);
        Assert.Equal("rate_limited", f.Code);
        Assert.Equal(1800, f.RetryAfterSeconds);
    }

    [Fact]
    public async Task An_account_error_takes_the_lane_down_without_retrying()
    {
        using var h = new Harness();
        h.Play(Lines.Init, """{"type":"system","subtype":"api_retry","error":"authentication_failed"}""", """{"type":"result","subtype":"success","is_error":true,"result":"Invalid API key"}""");
        var f = Assert.IsType<ModelEvent.Failure>((await h.RunAsync())[^1]);
        Assert.Equal("provider_error", f.Code);
        Assert.False(h.State.Current.Up);
        Assert.Contains("authentication_failed", h.State.Current.DownReason, StringComparison.Ordinal);
        Assert.Equal(1, h.Runner.Starts);
    }

    [Fact]
    public async Task Ending_without_a_result_is_a_dropped_stream()
    {
        using var h = new Harness();
        h.Play(Lines.Init, Lines.MessageStart, Lines.TextStart, Lines.Text1);
        Assert.Equal("stream_dropped", Assert.IsType<ModelEvent.Failure>((await h.RunAsync())[^1]).Code);
    }

    [Fact]
    public async Task A_quiet_child_times_out_and_is_killed()
    {
        using var h = new Harness(o => o.IdleTimeout = TimeSpan.FromMilliseconds(600));
        h.Play(Lines.Init, "#sleep 60000");
        var events = await h.RunAsync();
        Assert.Equal("timeout", Assert.IsType<ModelEvent.Failure>(events[^1]).Code);
        AssertGone(h.ReadDump().GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Cancelling_kills_the_child()
    {
        using var h = new Harness();
        h.Play(Lines.Init, "#sleep 60000");
        using var cts = new CancellationTokenSource();
        var run = h.RunCancellableAsync(cts.Token);
        await WaitForFileAsync(h.Dump);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        AssertGone(h.ReadDump().GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task The_hourly_cap_refuses_without_starting_a_process_and_clears_after_an_hour()
    {
        using var h = new Harness(o => o.MaxStepsPerHour = 1);
        h.Play(Lines.HappyPath);
        Assert.IsType<ModelEvent.Done>((await h.RunAsync())[^1]);
        var refused = Assert.IsType<ModelEvent.Failure>((await h.RunAsync()).Single());
        Assert.Equal("rate_limited", refused.Code);
        Assert.InRange(refused.RetryAfterSeconds!.Value, 3500, 3600);
        Assert.Equal(1, h.Runner.Starts);
        h.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.IsType<ModelEvent.Done>((await h.RunAsync())[^1]);
    }

    [Fact]
    public async Task A_bad_model_id_never_reaches_the_command_line()
    {
        using var h = new Harness();
        h.Play(Lines.HappyPath);
        var f = Assert.IsType<ModelEvent.Failure>((await h.RunAsync(model: "--dangerously-skip-permissions")).Single());
        Assert.Equal("provider_error", f.Code);
        Assert.Equal(0, h.Runner.Starts);
    }

    [Fact]
    public async Task Subscription_and_provider_credentials_never_reach_the_child_in_login_mode()
    {
        var saved = (Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"), Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"));
        Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", "sk-ant-oat-planted");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api-planted");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "planted");
        try
        {
            using var h = new Harness(o => o.EnvPassthrough = ["CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"]);
            h.Play(Lines.HappyPath);
            await h.RunAsync();
            var env = h.ReadDump().GetProperty("env");
            Assert.False(env.TryGetProperty("CLAUDE_CODE_OAUTH_TOKEN", out _));
            Assert.False(env.TryGetProperty("ANTHROPIC_API_KEY", out _));
            Assert.False(env.TryGetProperty("AWS_SECRET_ACCESS_KEY", out _));
            Assert.Equal(h.Options.ConfigDir, env.GetProperty("CLAUDE_CONFIG_DIR").GetString());
            Assert.DoesNotContain("--bare", h.ReadDump().GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", saved.Item1);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved.Item2);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", saved.Item3);
        }
    }

    [Fact]
    public async Task Api_key_mode_passes_only_the_resolved_key_and_uses_bare()
    {
        using var h = new Harness(o => { o.AuthMode = ClaudeCodeAuthMode.ApiKey; o.ApiKey = "env:X"; }, new FixedSecret("sk-ant-api-test-key"));
        h.Play(Lines.HappyPath);
        await h.RunAsync();
        var dump = h.ReadDump();
        Assert.Equal("sk-ant-api-test-key", dump.GetProperty("env").GetProperty("ANTHROPIC_API_KEY").GetString());
        Assert.Contains("--bare", dump.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.False(dump.GetProperty("env").TryGetProperty("CLAUDE_CONFIG_DIR", out _));
    }

    [Fact]
    public async Task A_subscription_token_in_the_api_key_reference_is_refused_and_not_echoed()
    {
        using var h = new Harness(o => { o.AuthMode = ClaudeCodeAuthMode.ApiKey; o.ApiKey = "env:X"; }, new FixedSecret("sk-ant-oat01-secret-token"));
        h.Play(Lines.HappyPath);
        var f = Assert.IsType<ModelEvent.Failure>((await h.RunAsync()).Single());
        Assert.DoesNotContain("secret-token", f.Message, StringComparison.Ordinal);
        Assert.Contains("subscription token", f.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Runner.Starts);
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; i < 200 && !File.Exists(path); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await Task.Delay(300, TestContext.Current.CancellationToken);
    }

    private static void AssertGone(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            Thread.Sleep(50);
        }

        Assert.Fail($"process {pid} is still running");
    }
}
