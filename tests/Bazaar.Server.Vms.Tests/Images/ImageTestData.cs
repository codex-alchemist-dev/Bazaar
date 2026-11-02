using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

/// <summary>Shared recipe and content fixtures for the image tests.</summary>
internal static class ImageTestData
{
    /// <summary>A valid Linux recipe with two pinned tools and guest agent 1.4.0.</summary>
    public static GoldenImageRecipe LinuxRecipe(int version = 3) => new(
        GoldenImageOs.Linux,
        version,
        "1.4.0",
        [new PinnedTool("git", "2.45.2"), new PinnedTool("dotnet-sdk", "11.0.100")]);

    /// <summary>SHA-256 of the empty input, lowercase hex.</summary>
    public const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>SHA-256 of the bytes of "abc", lowercase hex.</summary>
    public const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    /// <summary>SHA-256 of the UTF-8 bytes of the text, lowercase hex, computed independently of the code under test.</summary>
    public static string Sha256Of(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
