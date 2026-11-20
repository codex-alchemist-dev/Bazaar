using System.Reflection;

namespace Bazaar.Server.SignIn.Http;

/// <summary>Embedded sign-in page files. Exact manifest lookup only: nothing reaches the file system.</summary>
public static class SignInAssets
{
    private const string _prefix = "Bazaar.Server.SignIn.wwwroot.";

    private static readonly string[] _known = ["index.html", "assets/signin.js", "assets/signin.css"];

    public static bool TryGet(string path, out byte[] content, out string contentType)
    {
        content = [];
        contentType = string.Empty;
        if (!_known.Contains(path, StringComparer.Ordinal))
        {
            return false;
        }

        using var stream = typeof(SignInAssets).Assembly.GetManifestResourceStream(_prefix + path.Replace('/', '.'));
        if (stream is null)
        {
            return false;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        content = buffer.ToArray();
        contentType = path switch
        {
            _ when path.EndsWith(".html", StringComparison.Ordinal) => "text/html; charset=utf-8",
            _ when path.EndsWith(".css", StringComparison.Ordinal) => "text/css; charset=utf-8",
            _ => "text/javascript; charset=utf-8",
        };
        return true;
    }
}
