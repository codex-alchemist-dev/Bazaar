# K2: Linux bind-mount read-only slot and symlink pointer swap

Status: RESEARCHED (documentation read only). Container test NOT RUN: the research agent had no shell tool, so `unshare -m` was not executed and `/tmp/kodo-k2` was not created.

Design under test: `docs/design/46-kodo-health-module.md` section 6.2 (Linux: "the slot is read-only at the mount or bind level, and the update path remounts it for the swap") and section 6.3 (pointer file `current`, "on Linux, a symlink swap by rename"). Section 22 lists K2 as needed before Kodo immutability is built.

## Question

1. Can a Kodo runtime slot be made read-only at the bind-mount level on Linux, and what does the update path do to remount it for a swap?
2. Is a symlink swap by `rename(2)` atomic for the pointer file?
3. What mount-namespace and privilege needs apply, and what do systemd `ProtectSystem=` and `ReadOnlyPaths=` do to this design?

## Method

- Read the design doc sections 6 and 22 in the repo (primary).
- Fetched man7.org man pages for `rename(2)`, `renameat(2)`, `mount(2)`, `mount(8)`, `mount_namespaces(7)`, `namespaces(7)`, `umount(2)`.
- Fetched kernel.org shared-subtree documentation (the page did not contain the bind read-only text; treated as not verified).
- Fetched systemd `systemd.exec(5)` (the page is long; `ProtectSystem=`, `ReadOnlyPaths=`, `ReadWritePaths=`, `PrivateMounts=` were read; `BindReadOnlyPaths=` was not found in the portions fetched).
- One WebSearch on symlink swap practice (secondary; forum and Stack Exchange posts, weak evidence).
- Container test: not possible (no shell tool in this session).

Labels: **verified** = primary source text read in this session; **secondary** = forum or blog text; **unverified** = my own inference or not found in the source read.

## Findings

### F1. Read-only bind mount (verified)

- The classic sequence is `mount --bind olddir newdir` then `mount -o remount,bind,ro newdir`. Source: https://man7.org/linux/man-pages/man8/mount.8.html (DESCRIPTION, bind mount section). **verified**
- "A read-only bind will create a read-only mountpoint (VFS entry)", with the original superblock remaining writable. Source: same page (Read-only Setting Notes). **verified**
- Consequence for the design: the read-only flag is per mount point. Any other path to the same directory (for example the source directory, or another bind mount of it) stays writable. The slot's real location must not be writable by the Kodo service account. **verified** (per-mount scope) and **unverified** as a deployment rule (inference).
- `mount(2)`: "Since Linux 2.6.26, the MS_REMOUNT flag can be used with MS_BIND to modify only the per-mount-point flags." And `MS_REMOUNT | MS_BIND | MS_RDONLY` "will make access through this mount point read-only (clearing all other per-mount-point flags)". Source: https://man7.org/linux/man-pages/man2/mount.2.html (DESCRIPTION, Remounting). **verified**
- `MS_RDONLY` "can be set or cleared on a per-mount-point basis as well as on the underlying filesystem superblock." Same source. **verified**
- Recursion: `mount --rbind` attaches submounts too. The `mount(2)` call does not change mount options recursively (`-o rbind,ro` is not supported by the classic call). Source: https://man7.org/linux/man-pages/man8/mount.8.html (Recursive behavior). **verified**. Implication: a slot containing submounts needs a recursive read-only treatment, or the design must forbid submounts in a slot. **unverified** as to what kernel versions support recursive read-only (not checked).

### F2. How the update path remounts for a swap (verified for the primitives; the sequence is my inference)

- A bind mount's source is fixed when the mount is made. A remount changes only flags, not the source (mount(2) MS_REMOUNT text, same URL as F1). **verified** for "modifies only the per-mount-point flags".
- So "remount for the swap" can only mean, in practice: (a) make the inactive slot writable (`remount,rw`) for the update write, verify, then remount it read-only again; and/or (b) replace the bind mount at the active path (umount then bind, or stack a new bind on top).
- Design point to check: section 6.2 says the runtime slot is read-only, and section 6.3 says "the pointer is the only mutable item in the runtime area". If the pointer `current` sits inside a read-only slot mount, the flip cannot happen. The pointer must live in a writable directory outside both read-only slot mounts. **unverified** (inference from the two design sections; the design doc does not say where `current` lives).
- Stacking a new mount over an existing mount point and unmounting the old one is a common technique. I did not find a primary source for its visibility semantics in this session. **unverified**.
- Unmount: `umount2` `MNT_DETACH` makes "the mount unavailable for new accesses" and unmounts "when the mount ceases to be busy"; `EBUSY` is returned when the target "could not be unmounted because it is busy". Source: https://man7.org/linux/man-pages/man2/umount.2.html. **verified**. Implication: an unmount-then-bind swap can fail with EBUSY while Kodo has files open, so the swap must wait for or force the running process out first. **verified** (EBUSY), inference for the Kodo consequence.

### F3. Symlink swap by `rename(2)` (verified, with one caveat)

- "If newpath already exists, it will be atomically replaced, so that there is no point at which another process attempting to access newpath will find it missing." Source: https://man7.org/linux/man-pages/man2/rename.2.html (DESCRIPTION). **verified**
- The same page says "there will probably be a window in which both oldpath and newpath refer to the file being renamed." This is about hard links and is not a visibility gap for the pointer. **verified**
- "If newpath exists but the operation fails for some reason, rename() guarantees to leave an instance of newpath in place." Same source. **verified**
- Symlinks: "If oldpath refers to a symbolic link, the link is renamed; if newpath refers to a symbolic link, the link will be overwritten." Same source. **verified**. So renaming a new symlink over `current` is the pointer flip the design wants.
- Same-filesystem rule: `EXDEV` when "oldpath and newpath are not on the same mounted filesystem", and "rename() does not work across different mount points, even if the same filesystem is mounted on both." Same source. **verified**. Implication: the temp link and `current` must be in the same directory on the same mount, and a bind mount boundary between them gives EXDEV. **verified** (EXDEV text).
- `renameat2` with `RENAME_EXCHANGE`: "Atomically exchange oldpath and newpath. Both pathnames must exist but may be of different types." Introduced in Linux 3.15 with glibc 2.28. Source: https://man7.org/linux/man-pages/man2/renameat.2.html. **verified**. Useful only if the design keeps two named pointers that must both exist; it is not needed for the plain `current` flip. Not portable to all filesystems was not verified (the page does not say which).
- Caveat from secondary source: `ln -sfn` may unlink then create on some systems; use temp symlink plus `rename`/`mv -T` instead. Source: WebSearch results (Unix StackExchange and DEV Community posts, which conflict). **secondary**. Recommendation: write the link under a temp name and rename it over `current`, never `ln -sfn` directly.
- Not verified in this spike: whether `rename` durability needs an fsync of the parent directory after the flip (crash safety). This is a known practice but I did not find a primary source read here. **unverified**.
- Network filesystem caveat (one secondary source says rename atomicity may not hold on network filesystems). **secondary, unverified**. The Kodo slot is local.

### F4. Mount namespaces and privileges (verified)

- Creating namespaces with `clone(2)` or `unshare(2)` "in most cases requires the CAP_SYS_ADMIN capability". Source: https://man7.org/linux/man-pages/man7/namespaces.7.html. **verified**
- "User namespaces are the exception: since Linux 3.8, no privilege is required to create a user namespace." Same source. **verified**
- `mount(2)`: "Appropriate privilege (Linux: the CAP_SYS_ADMIN capability) is required to mount filesystems." Remount errors: `EPERM` when the caller "does not have the required privileges", and when a remount tries to change MS_RDONLY, MS_NOSUID or MS_NOEXEC on a "locked" mount (see mount_namespaces(7)). Source: https://man7.org/linux/man-pages/man2/mount.2.html (DESCRIPTION, ERRORS). **verified**
- `umount(2)` also requires CAP_SYS_ADMIN. Source: https://man7.org/linux/man-pages/man2/umount.2.html. **verified**
- Mount propagation: a new mount inherits shared propagation if its parent is shared, otherwise private. "systemd(1) automatically remounts all mounts as MS_SHARED on system startup", so the effective default on systemd hosts is usually shared. `unshare(1)` (util-linux 2.27+) makes mounts private in the new namespace by default; `--propagation unchanged` keeps the types. Source: https://man7.org/linux/man-pages/man7/mount_namespaces.7.html. **verified**
- Unprivileged user-namespace mounts: inherited mounts in a less-privileged user namespace are locked (they cannot be unmounted there) and shared mounts become slaves. Source: same page. **verified**. Implication: a rootless `unshare -Urm` test would show locked-mount behaviour, which is a different case from a root host run.

### F5. systemd ProtectSystem, ReadOnlyPaths, ReadWritePaths (verified text, with gaps)

Source: https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html

- `ProtectSystem=strict`: "the entire file system hierarchy is mounted read-only, except for the API file system subtrees" (`/dev/`, `/proc/`, `/sys/`). `full` adds `/etc/`. Boolean `true` covers `/usr/` and boot directories. **verified**
- `ProtectSystem=` "cannot ensure protection in all cases. In general it has the same limitations as `ReadOnlyPaths=`." **verified**
- `ReadOnlyPaths=`: "accessible for reading only, writing will be refused even if the usual file access controls would permit this." **verified**. The page says these settings "will disconnect propagation of mounts from the unit's processes to the host." **verified**. The fetched text does not say whether `ReadOnlyPaths=` is implemented as a bind mount. **unverified**.
- `ReadWritePaths=`: "Use `ReadWritePaths=` in order to allow-list specific paths for write access if `ProtectSystem=strict` is used." Limit: "`ReadWritePaths=` cannot be used to gain write access to a file system whose superblock is mounted read-only." **verified**. Implication: a writable allowlist cannot reopen a slot if the slot's superblock is read-only. Because F1 shows the read-only flag can be per mount point while the superblock stays writable, the exact interaction between `ReadWritePaths=` and a per-mount read-only bind is **unverified** and needs a host test.
- `PrivateMounts=` (in the sandboxing text): "a new CLONE_NEWNS namespace is created, after which all existing mounts are remounted to MS_SLAVE". **verified** (via the same page). Also: the `PrivateDevices=` and related options "apply the same restrictions regarding mount propagation and privileges as for `ReadOnlyPaths=`". **verified**.
- The `systemd.exec` text fetched did not contain `BindReadOnlyPaths=`. I did not verify it. **unverified**. It would be the usual way to get a read-only bind into a unit without giving the service CAP_SYS_ADMIN, so the next step should check its man page section.
- User services: "the various settings requiring file system namespacing support (such as `ProtectSystem=`) are not available." **verified**. Kodo runs as a system service, so this only matters if it is ever run as a user unit.

## Container test result

NOT RUN. This session had no shell tool (Bash was not available to the research agent). I did not run `unshare -m`, did not create `/tmp/kodo-k2`, and nothing was created or removed on disk except this notes file. Result: **inconclusive for the container**. Whether `unshare -m` works in the cloud container is still unknown.

## Open risks

1. **Where `current` lives.** The design says the pointer is the only mutable item in the runtime area, and the slot is read-only at mount level. These two rules conflict unless `current` is outside both read-only slot mounts. Needs a doc fix (design owner decision; I have not changed the doc).
2. **Remount for the swap.** A remount cannot change a bind mount's source (F2). The update flow must be written as explicit steps: unmount or stack, bind, remount read-only, re-verify. Each step needs CAP_SYS_ADMIN or a helper that has it.
3. **EBUSY on unmount.** An in-place swap fails while Kodo holds files open. Plan a stop or drain before the swap, or use a stacking method (unverified).
4. **Mount namespace visibility across units.** systemd `ReadOnlyPaths=` and `ProtectSystem=` build a private namespace for the service. A read-only remount made by a host-side updater may not be seen inside that namespace (mount copies are per namespace). **unverified**; needs a host test.
5. **Privilege.** Bind mount, remount and unmount need CAP_SYS_ADMIN (F4). A service without that capability cannot do the swap. Options: a small privileged helper, a systemd unit that does the bind (`BindReadOnlyPaths=`, unverified), or a root-run updater outside the Kodo service. Each affects the threat model in section 6.6 (a full root admin is out of scope there).
6. **Locked mounts in user namespaces.** Rootless testing shows locked-mount behaviour (F4), which does not match a root host.
7. **Recursive read-only.** Submounts inside a slot are not made read-only by a plain bind remount (F1). Forbid submounts in slots, or verify recursive behaviour on the target kernel.
8. **Durability.** Atomic rename does not by itself guarantee the flip survives a crash (directory fsync not verified here). **unverified**.
9. **Secondary sources only** for the `ln -sfn` behaviour and network-filesystem caveat. Use temp link plus rename, and do not rely on `ln -sfn`.
10. **Kernel and distro versions** of the target Linux host are not chosen yet. This note assumes a modern kernel (renameat2 needs 3.15+; per-mount remount needs 2.6.26+; user namespaces need 3.8+).

## What a host run would need to confirm

Run on the target Linux host (not the cloud container) as the Kodo service account, and again as root, and with systemd unit settings the design will use:

1. `mount --bind` of a slot directory to a read-only mount point, then `mount -o remount,bind,ro`. Confirm that a write through the mount point fails with EROFS, and that a write through the original path still succeeds (per-mount scope, F1).
2. Confirm that `remount,rw` then `remount,ro` on the bind works for the inactive slot update, and that `remount` cannot change the source.
3. Pointer flip: create a temp symlink in the same directory as `current` and `rename` it over `current` while a reader loops on `readlink`/`open`. Confirm no reader ever sees a missing path (F3). Repeat with `renameat2(RENAME_EXCHANGE)` if the design keeps two named pointers.
4. Confirm `EXDEV` when the temp link and `current` sit on different mounts (F3).
5. Swap with Kodo running: confirm the `EBUSY` behaviour of `umount` and the chosen drain or stack method (F2).
6. Under the real unit file: confirm whether `ProtectSystem=strict` and `ReadOnlyPaths=` block the updater's writes, whether a host-side remount is visible inside the unit namespace, and how `ReadWritePaths=` interacts with a per-mount read-only bind (F5 gaps). Check `BindReadOnlyPaths=` in the man page for the unit setup.
7. Confirm which capability set the updater and the service need (CAP_SYS_ADMIN for mount, umount and remount; none for rename of the pointer in a writable directory). Record the exact minimum.
8. Confirm the behaviour of `unshare -m` and `unshare -Urm` on the host kernel (it was not testable here).
9. Confirm the target filesystem (for example ext4 or XFS) supports the rename and symlink semantics used, and whether directory fsync is required for the flip to be durable.
10. Record the kernel version, distro, systemd version and the `ProtectSystem`/namespace behaviour as the K2 result. Then update section 22 of `docs/design/46-kodo-health-module.md` (owner or main session decision).

## Sources

- https://man7.org/linux/man-pages/man2/rename.2.html (verified)
- https://man7.org/linux/man-pages/man2/renameat.2.html (verified)
- https://man7.org/linux/man-pages/man2/mount.2.html (verified)
- https://man7.org/linux/man-pages/man8/mount.8.html (verified)
- https://man7.org/linux/man-pages/man2/umount.2.html (verified)
- https://man7.org/linux/man-pages/man7/mount_namespaces.7.html (verified)
- https://man7.org/linux/man-pages/man7/namespaces.7.html (verified)
- https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html (verified for the sections listed in F5; gaps noted)
- https://www.kernel.org/doc/html/latest/filesystems/sharedsubtree.html (fetched; did not contain the bind read-only text; not used as a source for any claim)
- WebSearch secondary results on symlink swap practice (DEV Community, Unix StackExchange, FreeBSD forums; secondary, conflicting; not used for any verified claim)
- Local: `/home/user/Bazaar/docs/design/46-kodo-health-module.md` sections 6.2, 6.3, 6.6, 22 (primary)
