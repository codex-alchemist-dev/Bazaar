// A stand-in for the claude program, for tests only (docs/design/91 section 13). It records how it was called and then plays a
// script. Script lines that start with '#' are directives; every other line is written to standard output as is.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var scriptPath = Environment.GetEnvironmentVariable("FAKE_CLI_SCRIPT");
var dumpPath = Environment.GetEnvironmentVariable("FAKE_CLI_DUMP");
var stdin = await Console.In.ReadToEndAsync();

string? ReadFileAfter(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length && File.Exists(args[i + 1]) ? File.ReadAllText(args[i + 1]) : null;
}

var dump = new Dictionary<string, object?>
{
    ["pid"] = Environment.ProcessId,
    ["args"] = args,
    ["stdin"] = stdin,
    ["cwd"] = Directory.GetCurrentDirectory(),
    ["env"] = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(k => k, k => k.Contains("KEY", StringComparison.OrdinalIgnoreCase) || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k == "CLAUDE_CONFIG_DIR" ? Environment.GetEnvironmentVariable(k) : "x"),
    ["systemPrompt"] = ReadFileAfter("--system-prompt-file"),
    ["mcpConfig"] = ReadFileAfter("--mcp-config"),
};

void Flush()
{
    if (dumpPath is not null)
    {
        File.WriteAllText(dumpPath, JsonSerializer.Serialize(dump));
    }
}

Flush();
if (scriptPath is null)
{
    return 0;
}

foreach (var line in File.ReadAllLines(scriptPath))
{
    if (!line.StartsWith('#'))
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
        continue;
    }

    var parts = line[1..].Split(' ', 2);
    switch (parts[0])
    {
        case "sleep":
            await Task.Delay(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "exit":
            return int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        case "stderr":
            Console.Error.WriteLine(parts[1]);
            break;
        case "mcp":
            var cfg = JsonElement.Parse((string)dump["mcpConfig"]!).GetProperty("mcpServers").GetProperty("bazaar");
            var url = cfg.GetProperty("url").GetString()!;
            var auth = cfg.GetProperty("headers").GetProperty("Authorization").GetString()!;
            using (var http = new HttpClient())
            {
                var results = new List<string>();
                foreach (var body in new[]
                {
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
                    """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                    """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{}}}""",
                })
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    req.Headers.Authorization = AuthenticationHeaderValue.Parse(auth);
                    results.Add(await (await http.SendAsync(req)).Content.ReadAsStringAsync());
                }

                using var bad = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                results.Add(((int)(await http.SendAsync(bad)).StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                dump["mcpResults"] = results;
            }

            Flush();
            break;
        default:
            break;
    }
}

return 0;
