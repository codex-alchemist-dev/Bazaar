# TLS and network setup for a Bazaar server

This guide is for the operator of a home or office Bazaar server. It covers the TLS certificate, when plain text is allowed, and how the network is reached. The rules come from `docs/design/32-remote-access-auth-and-privacy.md`, section 2.

No secret values belong in this guide or in configuration files. Certificates and keys are read from files, and passwords are read through secret references.

## Choose a certificate source

The setting `tls.certificate_source` picks where the server certificate comes from. The default is `pem`.

| Value | Status in this build |
|---|---|
| `pem` | Available. Use a PEM certificate and key, or a PFX file. |
| `acme` | Not yet available. Setting it makes the server refuse to start TLS. It does not fall back to another source. |
| `overlay` | Not yet available. The overlay-issued certificate is unverified in the design. |

Use `pem` until ACME or the overlay source ships.

## PEM certificate and key

1. Get a certificate chain and its private key. Keep the key readable only by the service account (`host.service_account`, never Administrator).
2. Store them as two files, for example `server.crt` (certificate, PEM) and `server.key` (unencrypted PKCS#8 or RSA PEM).
3. Point the server at both files with the PEM certificate and key paths in the setup wizard or advanced settings.
4. Restart the host. A certificate that fails to load stops the TLS listener and the error names the file, not the key.

A self-signed certificate is acceptable for a private network. Clients then pin the public key on first pairing (trust on first use). Replacing the key changes the pin, so the clients must pair again.

## PFX file

A PFX (PKCS#12) file holds the certificate and key together.

1. Store the PFX file where only the service account can read it.
2. If the file has a password, do not write the password into configuration. Store it in an environment variable, or in a file only the service account can read, and give the server the reference:
   - `env:NAME` reads the environment variable `NAME`.
   - `file:PATH` reads the whole file at `PATH`, without its trailing line ending.
3. Give the server the PFX path and the password reference.

If the password is wrong, or the reference cannot be resolved, the error names the reference and never the password.

## Plain text only on loopback

The server serves `ws://` (no TLS) only when it binds to a loopback address:

- `127.0.0.1` or any `127.x.x.x` address
- `::1`

Any other bind address, including a LAN address such as `192.168.x.x` or `0.0.0.0`, must use TLS. The server refuses a plain-text listener on those addresses. Use an IP literal to bind. A host name such as `localhost` is not resolved by the check, so bind `127.0.0.1` or `::1` instead.

## Network reach

- The network is never the only gate. Every device still signs in with its own credential, whatever network it comes from.
- Remote access over a private overlay network is the preferred route. The overlay gives reachability only, not identity.
- Port forwarding and dynamic DNS are discouraged. Expose the server only with TLS, and keep the sign-in page off unless you need it.

### Cloudflare Tunnel (operator option)

Cloudflare Tunnel is an option you may choose. It is not the default, and it is the weakest self-hosted posture:

- Cloudflare terminates TLS. Cloudflare can read the traffic in plain text, so a third party is in the TLS path.
- It needs no open port on your router, which helps behind carrier-grade NAT.
- WebSocket behaviour through the tunnel is not yet verified for Bazaar.

Choose it only if you accept that Cloudflare sees the plaintext traffic. Keep the server certificate in place either way.

## Checking your setup

- The host starts with `pem` and both files (or the PFX file) present.
- A plain-text listener on a non-loopback address is refused.
- The pin is the SHA-256 of the certificate's public key. Pairing (not yet built) records it, so a renewal with the same key keeps the pin.
- After a change of key, expect clients to pair again.
