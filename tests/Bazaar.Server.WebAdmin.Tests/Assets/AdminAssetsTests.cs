using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.WebAdmin.Assets;

namespace Bazaar.Server.WebAdmin.Tests.Assets;

public sealed class AdminAssetsTests
{
    private static readonly string[] _servedPaths = ["index.html", "assets/panel.css", "assets/panel.js"];

    private static string ReadText(string path)
    {
        Assert.True(AdminAssets.TryGet(path, out var content, out _), $"expected embedded asset '{path}'");
        return Encoding.UTF8.GetString(content);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("assets/panel.css")]
    [InlineData("assets/panel.js")]
    public void Embedded_panel_files_are_found(string path)
    {
        var found = AdminAssets.TryGet(path, out var content, out var contentType);

        Assert.True(found);
        Assert.NotEmpty(content);
        Assert.NotEmpty(contentType);
    }

    [Theory]
    [InlineData("../index.html")]
    [InlineData("assets/../index.html")]
    [InlineData("..")]
    [InlineData("/index.html")]
    [InlineData("/assets/panel.js")]
    [InlineData("assets\\panel.js")]
    [InlineData("assets//panel.js")]
    [InlineData("assets/panel.js\n")]
    [InlineData("")]
    public void Unsafe_paths_are_rejected(string path)
    {
        var found = AdminAssets.TryGet(path, out var content, out var contentType);

        Assert.False(found);
        Assert.Empty(content);
        Assert.Equal(string.Empty, contentType);
    }

    [Theory]
    [InlineData("missing.html")]
    [InlineData("assets/missing.js")]
    [InlineData("assets/panel.ts")]
    [InlineData("%2e%2e/index.html")]
    [InlineData("api/session")]
    public void Unknown_paths_are_rejected(string path)
    {
        Assert.False(AdminAssets.TryGet(path, out var content, out _));
        Assert.Empty(content);
    }

    [Fact]
    public void Content_types_match_the_extension()
    {
        AdminAssets.TryGet("index.html", out _, out var html);
        AdminAssets.TryGet("assets/panel.css", out _, out var css);
        AdminAssets.TryGet("assets/panel.js", out _, out var js);

        Assert.Equal("text/html; charset=utf-8", html);
        Assert.Equal("text/css; charset=utf-8", css);
        Assert.Equal("text/javascript; charset=utf-8", js);
    }

    [Fact]
    public void Repeated_lookups_return_the_same_bytes_and_type()
    {
        AdminAssets.TryGet("assets/panel.js", out var first, out var firstType);
        AdminAssets.TryGet("assets/panel.js", out var second, out var secondType);

        Assert.Equal(first, second);
        Assert.Equal(firstType, secondType);
    }

    // Doc 51 section 4 and the testing section: no external URL, no inline script or style, no markup writes from data.
    [Theory]
    [InlineData("index.html")]
    [InlineData("assets/panel.css")]
    [InlineData("assets/panel.js")]
    public void Served_asset_has_no_external_or_inline_hazards(string path)
    {
        var text = ReadText(path);
        var violations = new List<string>();

        foreach (var forbidden in new[]
                 {
                     "http://", "https://", "//cdn", "@import", "style=", "innerHTML", "eval(", "document.write", "<style",
                 })
        {
            if (text.Contains(forbidden, StringComparison.Ordinal))
            {
                violations.Add($"contains '{forbidden}'");
            }
        }

        foreach (Match m in Regex.Matches(text, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase))
        {
            violations.Add($"inline handler '{m.Value.Trim()}'");
        }

        foreach (Match m in Regex.Matches(text, @"<script\b(?![^>]*\bsrc\s*=)[^>]*>", RegexOptions.IgnoreCase))
        {
            violations.Add($"inline script tag '{m.Value}'");
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Index_references_only_same_origin_admin_assets()
    {
        var html = ReadText("index.html");

        var srcValues = Regex.Matches(html, @"\ssrc\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToList();
        var linkTags = Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        var linkHrefs = linkTags
            .Select(tag => Regex.Match(tag, @"\shref\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Contains("/admin/assets/panel.js", srcValues);
        Assert.Contains("/admin/assets/panel.css", linkHrefs);
        Assert.All(srcValues, v => Assert.StartsWith("/admin/assets/", v, StringComparison.Ordinal));
        Assert.All(linkHrefs, v => Assert.StartsWith("/admin/assets/", v, StringComparison.Ordinal));

        // Every other href is same-origin and relative to the server root.
        foreach (Match m in Regex.Matches(html, @"\shref\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase))
        {
            var value = m.Groups[1].Value;
            Assert.StartsWith("/", value, StringComparison.Ordinal);
            Assert.False(value.StartsWith("//", StringComparison.Ordinal), $"protocol-relative reference '{value}'");
            Assert.DoesNotContain("://", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Served_paths_list_is_complete()
    {
        // Guards the scan above: every file the panel ships is in the scanned set.
        foreach (var path in _servedPaths)
        {
            Assert.True(AdminAssets.TryGet(path, out _, out _), path);
        }
    }
}
