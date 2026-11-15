# Bazaar server host (Bazaar.Server.Host) RPM.
#
# Layout follows docs/design/30-linux-client-and-packaging.md (Linux server section):
#   /opt/bazaar/releases/<version>   one directory per release, side by side; NOT owned by the RPM
#   /opt/bazaar/current              symlink to the active release (repointed in %post)
#   /usr/share/bazaar-server/payload-<version>.tar   the package's release payload (owned by the RPM)
#   /etc/bazaar/bazaar.env           bootstrap configuration only (%config(noreplace))
#   /var/lib/bazaar                  state (data root)
#   %{_unitdir}/bazaar-server.service
#
# Why the payload is a tar: an RPM upgrade removes files owned by the old package. Extracting the
# release into /opt/bazaar/releases/<version> in %post, outside RPM ownership, keeps every installed
# release on disk, so rollback is only a symlink change.
#
# Rollback (operator-run, doc 30):
#   sudo systemctl stop bazaar-server
#   sudo ln -sfn /opt/bazaar/releases/<old-version> /opt/bazaar/current
#   sudo systemctl start bazaar-server
# A schema mismatch makes the older host refuse to start, which is the intended safety net.
# Old release directories are never removed automatically; delete them by hand when no longer needed.
#
# Status: UNVERIFIED. Not built or linted on a machine with rpmbuild (the authoring host is Windows).
# Database migrations are never run by this package. Run them explicitly before a restart:
#   sudo -u bazaar /opt/bazaar/current/Bazaar.Server.Host migrate

%global debug_package %{nil}

%ifarch x86_64
%global dotnet_rid linux-x64
%endif
%ifarch aarch64
%global dotnet_rid linux-arm64
%endif

Name:           bazaar-server
Version:        0.1.0
Release:        1%{?dist}
Summary:        Bazaar server host (headless, runs under systemd)

License:        AGPL-3.0-only
URL:            https://github.com/codex-alchemist-dev/Bazaar
# Source archive of the repository at the release tag, extracted to %{name}-%{version}/.
Source0:        %{name}-%{version}.tar.gz

# Self-contained publish, so no .NET runtime is required on the host. Native AOT is not used in v1.
ExclusiveArch:  x86_64 aarch64

BuildRequires:  dotnet-sdk-11.0
BuildRequires:  systemd-rpm-macros
BuildRequires:  git-core
Requires:       systemd
Requires(pre):  shadow-utils
Requires(post): tar

%description
Bazaar.Server.Host is the headless Bazaar server. It runs as the bazaar system user under
systemd (Type=notify), with a loopback-only status listener. It needs a PostgreSQL database,
configured in /etc/bazaar/bazaar.env. Database migrations run only through the explicit
"migrate" subcommand; the service refuses to start on a schema mismatch.

%prep
%autosetup -n %{name}-%{version}

%build
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
dotnet publish src/Bazaar.Server.Host/Bazaar.Server.Host.csproj \
    --configuration Release \
    --runtime %{dotnet_rid} \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:ContinuousIntegrationBuild=true \
    --output %{_builddir}/publish

%install
# Release payload, packed as a tar that %post extracts into its own versioned directory.
install -d %{buildroot}/opt/bazaar/releases
install -d %{buildroot}%{_datadir}/bazaar-server
tar -C %{_builddir}/publish -cf %{buildroot}%{_datadir}/bazaar-server/payload-%{version}.tar .

# Unit file and bootstrap configuration.
install -D -m 0644 packaging/linux-rpm/bazaar-server.service %{buildroot}%{_unitdir}/bazaar-server.service
install -d -m 0750 %{buildroot}/etc/bazaar
install -m 0640 packaging/linux-rpm/bazaar.env.example %{buildroot}/etc/bazaar/bazaar.env

# State directory (also created by systemd StateDirectory=).
install -d -m 0750 %{buildroot}/var/lib/bazaar

%pre
getent group bazaar >/dev/null || groupadd -r bazaar
getent passwd bazaar >/dev/null || \
    useradd -r -g bazaar -d /var/lib/bazaar -s /sbin/nologin -c "Bazaar server" bazaar
exit 0

%post
# Extract this release side by side with older ones. An existing directory is kept as is.
rel=/opt/bazaar/releases/%{version}
if [ ! -d "$rel" ]; then
    install -d /opt/bazaar/releases
    stage=$(mktemp -d /opt/bazaar/releases/.stage-XXXXXX)
    if tar -xf %{_datadir}/bazaar-server/payload-%{version}.tar -C "$stage"; then
        mv "$stage" "$rel"
    else
        rm -rf "$stage"
        exit 1
    fi
fi
# Point current at this release. Rolling back is repointing this symlink (see the header).
ln -sfn "$rel" /opt/bazaar/current
%systemd_post bazaar-server.service

%preun
%systemd_preun bazaar-server.service

%postun
# Restart is deliberately not automatic: upgrades are operator-run (doc 30). Run "migrate" first, then restart.
# Release directories under /opt/bazaar/releases are kept on upgrade and on erase, so rollback stays possible.
%systemd_postun bazaar-server.service
if [ $1 -eq 0 ]; then
    rm -f /opt/bazaar/current
fi
exit 0

%files
%license LICENSE
%dir /opt/bazaar
%dir /opt/bazaar/releases
%{_datadir}/bazaar-server/payload-%{version}.tar
%ghost /opt/bazaar/current
%{_unitdir}/bazaar-server.service
%dir %attr(0750, root, bazaar) /etc/bazaar
%config(noreplace) %attr(0640, root, bazaar) /etc/bazaar/bazaar.env
%dir %attr(0750, bazaar, bazaar) /var/lib/bazaar

%changelog
* Fri Oct 30 2026 The Bazaar Authors - 0.1.0-1
- Initial RPM spec for the Bazaar server host (systemd unit, bazaar user, /opt/bazaar release layout).
