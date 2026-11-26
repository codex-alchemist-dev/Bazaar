using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>Renders a setup report with the same plain-text renderer as the flag form, so wizard lines match <c>setup --dry-run</c>.</summary>
internal sealed class AdminSetupReportRenderer : ISetupReportRenderer
{
    public IReadOnlyList<string> RenderLines(SetupReport report)
    {
        using var writer = new StringWriter();
        AdminTextRenderer.Write(report, writer);
        return writer.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
    }
}
