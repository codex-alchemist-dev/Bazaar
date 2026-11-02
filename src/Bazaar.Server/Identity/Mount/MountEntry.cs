using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// One file to write into an agent's mounted folder. <see cref="Credential"/> names the credential class the content
/// came from: it is <c>null</c> for metadata and canaries, and <see cref="CredentialKind.Cookie"/> for a cookie profile.
/// <see cref="MountAllowList"/> validates every entry before it is written.
/// </summary>
public sealed record MountEntry(string FileName, MountedFileKind Kind, CredentialKind? Credential, ReadOnlyMemory<byte> Content);
