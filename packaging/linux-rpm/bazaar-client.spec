# Bazaar Linux client (Bazaar.Client.Linux) RPM.
#
# Layout (doc 30, section 3; doc 37): the self-contained publish output in /opt/bazaar-client, a /usr/bin/bazaar-client
# symlink, the desktop entry and the scalable icon. The spec packages a ready payload tree; it does not build anything.
# Run it through packaging/linux-rpm/build-rpm.sh, which stages the tree, fixes modes and times, and passes:
#   pkg_version   the product version (VersionPrefix in Directory.Build.props)
#   payload_root  the staged tree that %install copies into the build root
# Native AOT is not used; the bundled .NET runtime means the host needs no .NET install.

%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none
%global __os_install_post %{nil}

%if "%{?pkg_version}" == ""
%{error:pkg_version is not set; build through packaging/linux-rpm/build-rpm.sh}
%endif
%if "%{?payload_root}" == ""
%{error:payload_root is not set; build through packaging/linux-rpm/build-rpm.sh}
%endif

Name:           bazaar-client
Version:        %{pkg_version}
Release:        1%{?dist}
Summary:        Bazaar desktop client

License:        AGPL-3.0-only
URL:            https://github.com/codex-alchemist-dev/Bazaar
ExclusiveArch:  x86_64 aarch64
AutoReqProv:    no
Requires:       glibc
Requires:       libstdc++
Requires:       libgcc
Requires:       zlib

%description
Group chats and agents for your Bazaar server. The package bundles a self-contained .NET build, so no .NET runtime is
needed on the host. Installed under /opt/bazaar-client, with bazaar-client on the PATH.

%prep

%build

%install
mkdir -p %{buildroot}
cp -a %{payload_root}/. %{buildroot}/

%files
%defattr(-,root,root,-)
/opt/bazaar-client
/usr/bin/bazaar-client
/usr/share/applications/bazaar-client.desktop
/usr/share/icons/hicolor/scalable/apps/bazaar-client.svg

%changelog
* Thu Nov 19 2026 The Bazaar Authors <packaging@bazaar.invalid> - %{pkg_version}-1
- Client package built from the self-contained publish output.
