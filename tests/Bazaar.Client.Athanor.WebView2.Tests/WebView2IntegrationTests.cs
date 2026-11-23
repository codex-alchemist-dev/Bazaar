using System.Text.Json;
using Bazaar.Client.Athanor.Bridge;
using Bazaar.Client.Athanor.Bundle;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.WebView2.Tests;

/// <summary>
/// Runs the real bundle in a real WebView2 on a Win32 window. Skipped unless this is Windows with a WebView2 runtime and
/// the Monaco bundle is vendored. The profile lives next to the test binaries, never in a system folder.
/// </summary>
public sealed class WebView2IntegrationTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static void RequireRuntime()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WebView2 needs Windows.");
        Assert.SkipUnless(AthanorWebView2Environment.IsRuntimeAvailable(), "No WebView2 runtime is installed.");
        Assert.SkipUnless(AthanorBundle.Verify().Complete, "The Monaco bundle is not vendored (run scripts/fetch-monaco.ps1).");
    }

    private static WebView2HostOptions Options() => new()
    {
        UserDataFolder = Path.Combine(AppContext.BaseDirectory, "wv2-profile"),
        EnableDevToolsProtocol = true,
    };

    private static ThemeDefinition DarkTheme() => new(
        SchemaVersion: 1,
        Name: "Test dark",
        Base: ThemeBase.Dark,
        Tokens: new Dictionary<string, string>
        {
            ["color.background.editor"] = "#181825",
            ["color.accent.default"] = "#89B4FA",
            ["color.syntax.default"] = "#CDD6F4",
            ["color.syntax.keyword"] = "#CBA6F7",
            ["color.syntax.string"] = "#A6E3A1",
            ["color.syntax.comment"] = "#6C7086",
            ["color.syntax.number"] = "#FAB387",
            ["color.syntax.type"] = "#F9E2AF",
        });

    private static Task<T> Next<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return tcs.Task.WaitAsync(_wait);
    }

    private static async Task<JsonElement> Eval(WebView2EditorSurface surface, string expression)
    {
        string raw = await surface.CallDevToolsAsync("Runtime.evaluate", new { expression, returnByValue = true });
        using JsonDocument json = JsonDocument.Parse(raw);
        return json.RootElement.GetProperty("result").GetProperty("value").Clone();
    }

    private static async Task WaitForEditorFocus(WebView2EditorSurface surface)
    {
        for (int i = 0; i < 100; i++)
        {
            JsonElement focused = await Eval(surface, "document.activeElement?.className === 'native-edit-context'");
            if (focused.GetBoolean())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The editor never took focus.");
    }

    private static async Task Type(WebView2EditorSurface surface, string text)
    {
        foreach (char c in text)
        {
            string s = c.ToString();
            string code = char.IsLetter(c) ? "Key" + char.ToUpperInvariant(c) : char.IsDigit(c) ? "Digit" + c : string.Empty;
            int vk = char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : c == ' ' ? 32 : 0;
            await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = s, code, windowsVirtualKeyCode = vk, text = s, unmodifiedText = s });
            await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = s, code, windowsVirtualKeyCode = vk });
        }
    }

    private static async Task WithSurface(
        Func<WebView2EditorSurface, nint, Task> body,
        IEditorKeyHandoff? handoff = null,
        IClaimedKeySource? claims = null)
    {
        RequireRuntime();
        await Win32Pump.RunAsync(async window =>
        {
            await using var surface = new WebView2EditorSurface(Options(), handoff, claims);
            var ready = Next<bool>(set => surface.Host.Ready += (_, _) => set(true));
            await surface.AttachAsync(window);
            Assert.Null(surface.FailureMessage);
            surface.SetBounds(1000, 700);
            await ready;
            await surface.CallDevToolsAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }); // the test window is never the foreground window
            await body(surface, window);
        });
    }

    [Fact]
    public async Task Editor_loads_from_the_bundle_opens_a_document_and_reports_typed_changes()
    {
        await WithSurface(async (surface, _) =>
        {
            var doc = new EditorDocument(new EditorDocumentId("ws", "a.txt"), string.Empty, "rev-1", "plaintext", false);
            await surface.Host.OpenAsync(doc, CancellationToken.None);
            await surface.Host.FocusAsync(CancellationToken.None);
            surface.MoveFocusIntoEditor();
            var typed = new TaskCompletionSource<EditorChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            surface.Host.Changed += (_, c) =>
            {
                if (c.Content == "Hello, Bazaar! 123")
                {
                    typed.TrySetResult(c);
                }
            };

            await WaitForEditorFocus(surface);
            await Type(surface, "Hello, Bazaar! 123");

            EditorChange change = await typed.Task.WaitAsync(_wait);
            Assert.Equal(doc.Id, change.Id);
            Assert.True(change.Dirty);
            Assert.True(change.Version >= 1);
            Assert.Equal(0, surface.BlockedRequestCount);
            Assert.Equal(0, surface.Host.RejectedInboundCount);
        });
    }

    [Fact]
    public async Task Theme_from_the_generator_applies_without_a_reload_and_can_change_live()
    {
        await WithSurface(async (surface, _) =>
        {
            var theme = new EditorThemeProvider(DarkTheme);
            await surface.Host.OpenAsync(new EditorDocument(new EditorDocumentId("ws", "a.cs"), "class A { }", "r", "csharp", false), CancellationToken.None);

            await theme.PushAsync(surface.Host, CancellationToken.None);
            await Task.Delay(500);
            JsonElement dark = await Eval(surface, "getComputedStyle(document.querySelector('.monaco-editor-background')).backgroundColor");

            Assert.Equal("rgb(24, 24, 37)", dark.GetString());

            ThemeDefinition lighter = DarkTheme() with { Tokens = new Dictionary<string, string>(DarkTheme().Tokens) { ["color.background.editor"] = "#FFFFFF" } };
            await new EditorThemeProvider(() => lighter).PushAsync(surface.Host, CancellationToken.None);
            await Task.Delay(500);
            JsonElement white = await Eval(surface, "getComputedStyle(document.querySelector('.monaco-editor-background')).backgroundColor");

            Assert.Equal("rgb(255, 255, 255)", white.GetString());
        });
    }

    [Fact]
    public async Task Claimed_key_is_swallowed_and_reported_but_a_ctrl_chord_stays_in_the_editor()
    {
        var claimed = new List<EditorKeyEvent>();
        var handoff = new RouterKeyHandoff(k =>
        {
            claimed.Add(k);
            return true;
        });
        var claims = new StaticClaims("Alt+KeyM", "F6");
        await WithSurface(
            async (surface, _) =>
            {
                await surface.Host.OpenAsync(new EditorDocument(new EditorDocumentId("ws", "a.txt"), string.Empty, "r", "plaintext", false), CancellationToken.None);
                surface.MoveFocusIntoEditor();
                await Task.Delay(300); // let setClaimedKeys arrive
                var swallowed = Next<EditorKeyEvent>(set => surface.Host.KeyClaimed += (_, k) => set(k));

                await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "m", code = "KeyM", windowsVirtualKeyCode = 77, modifiers = 1 });
                await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "m", code = "KeyM", windowsVirtualKeyCode = 77, modifiers = 1 });

                EditorKeyEvent key = await swallowed;
                Assert.Equal("KeyM", key.Key);
                Assert.True(key.Alt);
                Assert.Single(claimed);

                int before = claimed.Count;
                await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "s", code = "KeyS", windowsVirtualKeyCode = 83, modifiers = 2 });
                await surface.CallDevToolsAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "s", code = "KeyS", windowsVirtualKeyCode = 83, modifiers = 2 });
                await Task.Delay(300);
                Assert.Equal(before, claimed.Count);
            },
            handoff,
            claims);
    }

    [Fact]
    public async Task Stale_host_seq_is_refused_by_the_page_and_reported()
    {
        await WithSurface(async (surface, _) =>
        {
            await surface.Host.FocusAsync(CancellationToken.None); // the host's own seq 1; the page has now seen it
            var error = Next<ErrorMessage>(set => surface.Host.ErrorReported += (_, e) => set(e));

            surface.Core!.PostWebMessageAsString("{\"type\":\"focus\",\"seq\":1}"); // the host already used seq 1 or more

            ErrorMessage reported = await error;
            Assert.Equal("stale-seq", reported.Code);
        });
    }

    [Fact]
    public async Task Diff_shows_the_side_by_side_editor()
    {
        await WithSurface(async (surface, _) =>
        {
            await surface.Host.ShowDiffAsync("a\nb\nc\n", "a\nB\nc\nd\n", "plaintext", CancellationToken.None);
            await Task.Delay(1500);

            JsonElement present = await Eval(surface, "!!document.querySelector('.monaco-diff-editor')");

            Assert.True(present.GetBoolean());
        });
    }

    [Fact]
    public async Task Navigation_away_from_the_bundle_origin_is_blocked()
    {
        await WithSurface(async (surface, _) =>
        {
            await surface.CallDevToolsAsync("Runtime.evaluate", new { expression = "location.href = 'https://example.com/'" });
            await Task.Delay(800);

            JsonElement host = await Eval(surface, "location.host");

            Assert.Equal(WebView2HostOptions.VirtualHost, host.GetString());
            Assert.True(surface.BlockedRequestCount >= 1);
        });
    }

    [Fact]
    public async Task Renderer_crash_reloads_the_page_and_the_host_resends_the_document_with_its_latest_text()
    {
        await WithSurface(async (surface, _) =>
        {
            var doc = new EditorDocument(new EditorDocumentId("ws", "a.txt"), "start", "r", "plaintext", false);
            await surface.Host.OpenAsync(doc, CancellationToken.None);
            var edited = Next<EditorChange>(set => surface.Host.Changed += (_, c) => set(c));
            await surface.Host.FocusAsync(CancellationToken.None);
            surface.MoveFocusIntoEditor();
            await WaitForEditorFocus(surface);
            await Type(surface, "X");
            await edited;

            var crashed = Next<EditorCrashReason>(set => surface.Host.Crashed += (_, r) => set(r));
            var readyAgain = Next<bool>(set => surface.Host.Ready += (_, _) => set(true));
            try
            {
                await surface.CallDevToolsAsync("Page.crash", new { });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // The page died under the call, so the call itself fails; the crash is what the test is after.
            }


            Assert.Equal(EditorCrashReason.RendererExited, await crashed);
            Assert.True(await readyAgain);
            Assert.False(surface.Host.IsCrashed);
            await Task.Delay(500);
            JsonElement text = await Eval(surface, "document.querySelector('.view-lines')?.innerText ?? ''");
            Assert.Contains("X", text.GetString(), StringComparison.Ordinal);
        });
    }

    private sealed class StaticClaims(params string[] chords) : IClaimedKeySource
    {
        public IReadOnlyList<string> Chords { get; } = chords;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
