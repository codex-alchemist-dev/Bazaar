using System.Text.Json;

using Bazaar.Contracts.Engines;

namespace Bazaar.Contracts.Tests.Engines;

public class EngineContractsTests
{
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, EngineJson.Options);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, EngineJson.Options) ?? throw new InvalidOperationException("null JSON");

    private static JsonElement Element(string json) => JsonElement.Parse(json);

    [Fact]
    public void Manifest_round_trips_with_optional_fields()
    {
        var manifest = new EngineManifest(
            EngineId: "godot",
            Version: "4.7.2",
            Capabilities: [EngineCapability.Scenes, EngineCapability.HotReload, EngineCapability.ScriptCheck],
            NeedsGuiHost: false,
            BuildVariant: "dotnet",
            BinaryPath: "/opt/godot/godot");

        var json = Serialize(manifest);
        var back = Deserialize<EngineManifest>(json);

        Assert.Equal("godot", back.EngineId);
        Assert.Equal("4.7.2", back.Version);
        Assert.Equal([EngineCapability.Scenes, EngineCapability.HotReload, EngineCapability.ScriptCheck], back.Capabilities);
        Assert.False(back.NeedsGuiHost);
        Assert.Equal("dotnet", back.BuildVariant);
        Assert.Equal("/opt/godot/godot", back.BinaryPath);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Manifest_optional_fields_default_to_null_and_are_omitted()
    {
        var manifest = new EngineManifest("roblox", "0.1.0", [EngineCapability.Playtest], NeedsGuiHost: true);

        var json = Serialize(manifest);
        var back = Deserialize<EngineManifest>(json);

        Assert.DoesNotContain("build_variant", json);
        Assert.DoesNotContain("binary_path", json);
        Assert.Null(back.BuildVariant);
        Assert.Null(back.BinaryPath);
        Assert.True(back.NeedsGuiHost);
    }

    [Fact]
    public void Capability_and_risk_enums_serialize_as_snake_case()
    {
        Assert.Equal("\"hot_reload\"", Serialize(EngineCapability.HotReload));
        Assert.Equal("\"script_check\"", Serialize(EngineCapability.ScriptCheck));
        Assert.Equal("\"scenes\"", Serialize(EngineCapability.Scenes));
        Assert.Equal("\"playtest\"", Serialize(EngineCapability.Playtest));
        Assert.Equal("\"exec\"", Serialize(RiskClass.Exec));
        Assert.Equal("\"write\"", Serialize(RiskClass.Write));
        Assert.Equal("\"read\"", Serialize(RiskClass.Read));
    }

    [Fact]
    public void Every_capability_round_trips_by_name()
    {
        foreach (var capability in Enum.GetValues<EngineCapability>())
        {
            Assert.Equal(capability, Deserialize<EngineCapability>(Serialize(capability)));
        }
    }

    [Fact]
    public void Pane_descriptor_golden_json_matches_exactly()
    {
        var pane = new PaneDescriptor(
            Kind: "godot.scene-tree",
            Title: "Scene tree",
            Capability: EngineCapability.Scenes,
            Model: Element("{\"root\":\"Main\",\"children\":[]}"),
            Revision: 3);

        const string golden =
            "{\"kind\":\"godot.scene-tree\",\"title\":\"Scene tree\",\"capability\":\"scenes\"," +
            "\"model\":{\"root\":\"Main\",\"children\":[]},\"revision\":3}";

        Assert.Equal(golden, Serialize(pane));
    }

    [Fact]
    public void Pane_descriptor_round_trips_and_defaults_revision_to_zero()
    {
        var pane = new PaneDescriptor("roblox.explorer", "Explorer", EngineCapability.Scenes, Element("[1,2]"));

        var json = Serialize(pane);
        var back = Deserialize<PaneDescriptor>(json);

        Assert.Contains("\"revision\":0", json);
        Assert.Equal("roblox.explorer", back.Kind);
        Assert.Equal("Explorer", back.Title);
        Assert.Equal(EngineCapability.Scenes, back.Capability);
        Assert.Equal(0, back.Revision);
        Assert.Equal("[1,2]", back.Model.GetRawText());
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Command_round_trips_with_and_without_arguments()
    {
        var withArgs = new EngineCommand("cmd-1", "export", Element("{\"preset\":\"Linux\",\"dir\":\"out\"}"));
        var withoutArgs = new EngineCommand("cmd-2", "import");

        var json = Serialize(withArgs);
        var back = Deserialize<EngineCommand>(json);
        Assert.Equal("cmd-1", back.CommandId);
        Assert.Equal("export", back.Name);
        Assert.Equal("{\"preset\":\"Linux\",\"dir\":\"out\"}", back.Arguments!.Value.GetRawText());
        Assert.Equal(json, Serialize(back));

        var bare = Serialize(withoutArgs);
        Assert.DoesNotContain("arguments", bare);
        Assert.Null(Deserialize<EngineCommand>(bare).Arguments);
    }

    [Fact]
    public void Command_result_round_trips()
    {
        var result = new CommandResult("cmd-1", 1, "Parsing failed", ["SCRIPT ERROR: unexpected token"]);

        var json = Serialize(result);
        var back = Deserialize<CommandResult>(json);

        Assert.Equal("cmd-1", back.CommandId);
        Assert.Equal(1, back.ExitCode);
        Assert.Equal("Parsing failed", back.Output);
        Assert.Equal(["SCRIPT ERROR: unexpected token"], back.Errors);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Project_envelope_round_trips()
    {
        var envelope = new EngineProjectEnvelope("proj-9", "cmd-4", 17);

        var json = Serialize(envelope);
        var back = Deserialize<EngineProjectEnvelope>(json);

        Assert.Equal(envelope, back);
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Request_writes_jsonrpc_2_0_and_round_trips()
    {
        var request = new JsonRpcRequest("7", EngineMethods.Command, Element("{\"command_id\":\"cmd-1\"}"));

        var json = Serialize(request);
        var back = Deserialize<JsonRpcRequest>(json);

        Assert.Contains("\"jsonrpc\":\"2.0\"", json);
        Assert.Equal("7", back.Id);
        Assert.Equal("command", back.Method);
        Assert.Equal("{\"command_id\":\"cmd-1\"}", back.Params!.Value.GetRawText());
    }

    [Fact]
    public void Request_without_params_omits_params_member()
    {
        var json = Serialize(new JsonRpcRequest("1", EngineMethods.Health));

        Assert.Equal("{\"id\":\"1\",\"method\":\"health\",\"jsonrpc\":\"2.0\"}", json);
    }

    [Fact]
    public void Success_response_omits_error_and_error_response_omits_result()
    {
        var success = new JsonRpcResponse("7", Result: Element("{\"ok\":true}"));
        var failure = new JsonRpcResponse("7", Error: new JsonRpcError(EngineErrorCodes.Timeout, "Timed out"));

        var successJson = Serialize(success);
        var failureJson = Serialize(failure);

        Assert.Equal("{\"id\":\"7\",\"result\":{\"ok\":true},\"jsonrpc\":\"2.0\"}", successJson);
        Assert.DoesNotContain("\"error\"", successJson);
        Assert.DoesNotContain("\"result\"", failureJson);
        Assert.Contains("\"jsonrpc\":\"2.0\"", failureJson);

        var back = Deserialize<JsonRpcResponse>(failureJson);
        Assert.Equal(EngineErrorCodes.Timeout, back.Error!.Code);
        Assert.Equal("Timed out", back.Error.Message);
        Assert.Null(back.Error.Data);
        Assert.Null(back.Result);
    }

    [Fact]
    public void Notification_writes_jsonrpc_and_has_no_id()
    {
        var notification = new JsonRpcNotification(EngineMethods.Output, Element("{\"command_id\":\"cmd-1\",\"seq\":4,\"text\":\"hi\"}"));

        var json = Serialize(notification);
        var back = Deserialize<JsonRpcNotification>(json);

        Assert.Equal("{\"method\":\"output\",\"params\":{\"command_id\":\"cmd-1\",\"seq\":4,\"text\":\"hi\"},\"jsonrpc\":\"2.0\"}", json);
        Assert.DoesNotContain("\"id\"", json);
        Assert.Equal("output", back.Method);
    }

    [Fact]
    public void Error_round_trips_with_data()
    {
        var error = new JsonRpcError(EngineErrorCodes.CapabilityNotGranted, "No grant", Element("{\"tool\":\"execute_luau\"}"));

        var json = Serialize(error);
        var back = Deserialize<JsonRpcError>(json);

        Assert.Equal(EngineErrorCodes.CapabilityNotGranted, back.Code);
        Assert.Equal("No grant", back.Message);
        Assert.Equal("{\"tool\":\"execute_luau\"}", back.Data!.Value.GetRawText());
        Assert.Equal(json, Serialize(back));
    }

    [Fact]
    public void Method_names_and_error_codes_match_the_design()
    {
        Assert.Equal("open", EngineMethods.Open);
        Assert.Equal("command", EngineMethods.Command);
        Assert.Equal("cancel", EngineMethods.Cancel);
        Assert.Equal("close", EngineMethods.Close);
        Assert.Equal("health", EngineMethods.Health);
        Assert.Equal("output", EngineMethods.Output);

        Assert.Equal(-32010, EngineErrorCodes.AdapterUnavailable);
        Assert.Equal(-32011, EngineErrorCodes.Timeout);
        Assert.Equal(-32012, EngineErrorCodes.Cancelled);
        Assert.Equal(-32013, EngineErrorCodes.CapabilityNotGranted);
        Assert.Equal(-32014, EngineErrorCodes.AdapterCrashed);
    }
}
