using System.Text;
using System.Text.Json;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>One keyboard step: <c>type</c> (text), or <c>key</c>, <c>press</c>, <c>release</c> (a virtual-key code).</summary>
internal readonly record struct KeyStep(string Op, string? Text, int Code)
{
    public static KeyStep Type(string text) => new("type", text, 0);

    public static KeyStep Key(int code) => new("key", null, code);

    public static KeyStep Press(int code) => new("press", null, code);

    public static KeyStep Release(int code) => new("release", null, code);
}

/// <summary>One mouse step: <c>move</c> (x, y), <c>click</c> (button index 1 to 3) or <c>scroll</c> (delta).</summary>
internal readonly record struct MouseStep(string Op, int X, int Y, int Button, int Delta)
{
    public static MouseStep Move(int x, int y) => new("move", x, y, 0, 0);

    public static MouseStep Click(int button) => new("click", 0, 0, button, 0);

    public static MouseStep Scroll(int delta) => new("scroll", 0, 0, 0, delta);
}

/// <summary>Writes step lists as JSON with <see cref="Utf8JsonWriter"/>. The JSON is data for the script, never code.</summary>
internal static class HyperVSteps
{
    public static string ToJson(IReadOnlyList<KeyStep> steps) => Write(w =>
    {
        foreach (KeyStep s in steps)
        {
            w.WriteStartObject();
            w.WriteString("op", s.Op);
            if (s.Text is not null)
                w.WriteString("text", s.Text);
            else
                w.WriteNumber("code", s.Code);
            w.WriteEndObject();
        }
    });

    public static string ToJson(IReadOnlyList<MouseStep> steps) => Write(w =>
    {
        foreach (MouseStep s in steps)
        {
            w.WriteStartObject();
            w.WriteString("op", s.Op);
            switch (s.Op)
            {
                case "move":
                    w.WriteNumber("x", s.X);
                    w.WriteNumber("y", s.Y);
                    break;
                case "click":
                    w.WriteNumber("button", s.Button);
                    break;
                default:
                    w.WriteNumber("delta", s.Delta);
                    break;
            }

            w.WriteEndObject();
        }
    });

    private static string Write(Action<Utf8JsonWriter> items)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            items(w);
            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
