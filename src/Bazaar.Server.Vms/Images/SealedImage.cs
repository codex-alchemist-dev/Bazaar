namespace Bazaar.Server.Vms.Images;

/// <summary>
/// A parent image as recorded after sealing: what was built (OS, version, guest agent) and the hash and path it must still
/// match. Children record the SHA-256 of their parent, and verification compares the file against this record.
/// </summary>
public sealed record SealedImage(
    GoldenImageOs Os,
    int Version,
    string Sha256,
    string GuestAgentVersion,
    string Path);
