# Homebrew cask for the Bazaar client on macOS. NOT submitted to Homebrew.
#
# PLACEHOLDERS: the version, url, homepage and sha256 below are not real.
# Replace them with the released values (url must point at the Bazaar-Client-<version>.dmg
# asset on the GitHub Release; sha256 is that file's SHA-256). Tags are plain SemVer
# with a v prefix (docs/VERSIONING.md section 1), so 0.1.0 is tagged v0.1.0.
#
# The package is unsigned until the owner supplies an Apple Developer ID (doc 37, section 7).
# Homebrew will refuse an unsigned pkg unless the user approves it in System Settings.

cask "bazaar-client" do
  version "0.1.0"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://example.com/PLACEHOLDER/releases/download/v0.1.0/Bazaar-Client-#{version}.dmg"
  name "Bazaar Client"
  desc "Terminal client for Bazaar"
  homepage "https://example.com/PLACEHOLDER"

  # TODO(owner): minimum macOS version, to match the .NET runtime support policy.
  pkg "Bazaar-Client-#{version}.pkg"

  # Must match the --identifier used by packaging/macos/build-pkg.sh.
  uninstall pkgutil: "dev.bazaar.client",
            delete:  [
              "/usr/local/lib/bazaar",
              "/usr/local/bin/bazaar-client",
            ]
end
