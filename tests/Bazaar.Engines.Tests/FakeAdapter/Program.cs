using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Engines;

// A fake engine adapter for the AdapterHost tests. It speaks the JSON-RPC contract over stdio.
// Usage: FakeAdapter <mode> [slowMilliseconds]
//   normal: every command echoes two output lines and succeeds.
//   hang:   commands print two output lines and never reply. Other methods still work.
//   crash:  a command prints one output line and the process exits with code 3 without replying.
//   slow:   commands reply after slowMilliseconds.
//   unresponsive: prints one line at startup, then never replies to any request.
var mode = args.Length > 0 ? args[0] : "normal";
var slowMilliseconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 0;

var utf8 = new UTF8Encoding(false);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

var seq = new Dictionary<string, long>(StringComparer.Ordinal);

if (mode == "unresponsive")
{
    EmitOutput("boot", "startup", "booting");
}

string? line;
while ((line = stdin.ReadLine()) is not null)
{
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    var method = root.GetProperty("method").GetString();
    var hasId = root.TryGetProperty("id", out var idElement);
    var id = hasId ? idElement.GetString() : null;
    var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

    if (mode == "unresponsive")
    {
        continue;
    }

    switch (method)
    {
        case EngineMethods.Health:
            Reply(id, new JsonObject { ["status"] = "ok", ["mode"] = mode });
            break;

        case EngineMethods.Open:
        case EngineMethods.Cancel:
            Reply(id, new JsonObject { ["ok"] = true });
            break;

        case EngineMethods.Close:
            Reply(id, new JsonObject { ["ok"] = true });
            return 0;

        case EngineMethods.Command:
            HandleCommand(id, parameters);
            break;
    }
}

return 0;

void HandleCommand(string? requestId, JsonElement parameters)
{
    var projectId = parameters.GetProperty("project_id").GetString()!;
    var commandId = parameters.GetProperty("command_id").GetString()!;
    var name = parameters.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : string.Empty;

    switch (mode)
    {
        case "hang":
            EmitOutput(projectId, commandId, "step 1");
            EmitOutput(projectId, commandId, "step 2");
            return;

        case "crash":
            EmitOutput(projectId, commandId, "about to fail");
            Environment.Exit(3);
            return;

        case "slow":
            Thread.Sleep(slowMilliseconds);
            break;
    }

    EmitOutput(projectId, commandId, $"{name} line 1");
    EmitOutput(projectId, commandId, $"{name} line 2");
    var result = new CommandResult(commandId, 0, $"{name} line 1\n{name} line 2", []);
    Reply(requestId, JsonSerializer.SerializeToNode(result, EngineJson.Options));
}

void EmitOutput(string projectId, string commandId, string text)
{
    seq[projectId] = seq.GetValueOrDefault(projectId) + 1;
    var parameters = new JsonObject
    {
        ["project_id"] = projectId,
        ["command_id"] = commandId,
        ["seq"] = seq[projectId],
        ["text"] = text,
    };
    Write(new JsonRpcNotification(EngineMethods.Output, JsonSerializer.SerializeToElement(parameters, EngineJson.Options)));
}

void Reply(string? requestId, JsonNode? result)
{
    var element = result is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(result, EngineJson.Options);
    Write(new JsonRpcResponse(requestId, element));
}

void Write(object message) => stdout.WriteLine(JsonSerializer.Serialize(message, EngineJson.Options));
