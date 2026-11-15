using Android.Content;
using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Android.Pairing;

/// <summary>The Android device's refresh-credential store: Keystore-sealed, kept in app-private storage (doc 32, section 5).</summary>
public static class AndroidRefreshCredentials
{
    /// <summary>The Keystore alias of the sealing key. Changing it orphans stored credentials, which then force re-pairing.</summary>
    public const string KeyAlias = "bazaar.device.refresh-credential";

    /// <summary>The file, inside the app's private files directory, that holds the sealed credential.</summary>
    public const string FileName = "device-refresh-credential.bin";

    public static IRefreshCredentialStore Create(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var directory = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("The app has no private files directory.");
        return new SealedRefreshCredentialStore(
            new KeystoreCredentialSealer(KeyAlias),
            new AppPrivateBlobStore(Path.Combine(directory, FileName)));
    }
}
