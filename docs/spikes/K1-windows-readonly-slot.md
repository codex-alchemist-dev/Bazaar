# Spike K1: Windows read-only slot and junction swap

Status: RESEARCHED (not run on hardware). No code was built or run. Owner's PC hardware run is still required before any DECIDED status.

Related: `docs/design/46-kodo-health-module.md` section 6.2 (read-only runtime), 6.3 (A/B slots, pointer swap), 6.6 (stated limits), section 22 (K1 listed as needed before the Immutability row can move past PLANNED).

Evidence labels used below: **verified** (primary Microsoft page read in this session), **secondary** (search snippet or non-Microsoft source), **unverified** (no source read; must be tested).

## 1. Question

Two questions from doc 46:

1. Windows read-only slot (6.2): how to make a Kodo runtime directory read and execute for the Kodo service account and Administrators, deny write to everyone except the installer's signed update path, using ACL grants and denies with inheritance.
2. Atomic pointer swap (6.3): the doc says the pointer is written as a temp item then renamed, and on Windows "a directory junction swap with the same rule". Does a temp junction plus rename-over-the-old-junction give an atomic swap on NTFS? Does `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` work on junctions?

## 2. Method

- Research only. No hardware run, no VM, no Windows host used.
- Used WebSearch to locate pages. Every claim below is backed by a WebFetch of the Microsoft Learn page, unless marked secondary or unverified.
- Several pages returned 404 to WebFetch (the `FileRenameInfoEx` reference and the "How DACLs" access-check reference under a different slug). Those claims are marked unverified or sourced from a different page.
- The OwnerRights source is an archived Windows Vista-era page (verified text, but archived; treat as historical behaviour, re-check on Windows 11 / Server 2025 in the hardware run).
- No Windows API was called. No claim here is a measured behaviour.

## 3. Findings

### 3.1 The read-only attribute does not protect directories

- **verified.** `SetFileAttributes` reference: for `FILE_ATTRIBUTE_READONLY`, "This attribute is not honored on directories." https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileattributesa
- Consequence: the read-only slot cannot be a directory attribute. It must be an ACL.

### 3.2 DACL evaluation: order matters, first match wins

- **verified.** The system "examines each ACE in sequence until" an access-denied ACE denies a requested right, or access-allowed ACEs grant all requested rights, or the end is reached (implicit deny). https://learn.microsoft.com/en-us/windows/win32/secauthz/how-dacls-control-access-to-an-object
- **verified.** Preferred canonical order: (1) explicit ACEs before inherited ACEs; (2) within explicit, access-denied before access-allowed; (3) inherited by level, denied before allowed. "The preferred order provides a simple framework that ensures that an access-denied ACE actually denies access." https://learn.microsoft.com/en-us/windows/win32/secauthz/order-of-aces-in-a-dacl
- **verified.** Directly applied ACEs take precedence over inherited ACEs because they are placed ahead of them. https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces

Design consequence (important): a deny ACE for Everyone (`S-1-1-0`) on write rights cannot be "excepted" for the installer with a later allow ACE. Every token includes the Everyone group, the deny is evaluated first, and the installer is denied. The design text in 6.2 ("denies write to everyone except the installer") is not implementable as a deny-Everyone plus allow-installer pair. Use allow-list only (see 4.1).

### 3.3 Protected DACL stops inheritance from parents

- **verified.** `SetNamedSecurityInfo` and `SetSecurityInfo` propagate inheritable ACEs to children; "To ensure that a child object with no DACL is not affected by inheritable ACEs, set the SE_DACL_PROTECTED flag in the security descriptor of the object." https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces
- Consequence: the slot root must be created with a protected DACL, so a write ACE on a parent such as `ProgramData` cannot reach the slot.

### 3.4 Owner implicit rights (why an ACL alone is not tamper-proof)

- **verified (archived Vista-era page).** "the owner of a securable object ... always had the READ_CONTROL and WRITE_DAC rights implicitly granted. WRITE_DAC is a powerful right that can be used to change the access control policy. In Windows Vista, a new SID, OwnerRights, can be used in ACEs to control these rights." If the DACL contains OwnerRights ACEs, "the access check will use those entries". https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-vista/cc749445(v=ws.10)
- Consequence: a slot created by an elevated installer is owned by a group the installer's token uses (typically Administrators). The owner can rewrite the DACL unless an OwnerRights (`S-1-3-4`) ACE is present. Adding OwnerRights to the slot DACL is the mitigation, to be confirmed in the hardware run.
- Note: the well-known SIDs page did not list OwnerRights in the text read in this session. The OwnerRights definition above is from the archived page only. https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids (verified, page read; OwnerRights absent from the table text fetched)

### 3.5 Backup and restore privileges override ACLs

- **verified.** The `SE_BACKUP_NAME` and `SE_RESTORE_NAME` privileges let backup applications "override the standard security checking" when the call uses `FILE_FLAG_BACKUP_SEMANTICS`. https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights
- **verified.** Privileges are assigned per account by administrators and "the system checks the user's access token ... and if so, it checks whether the privileges are enabled." https://learn.microsoft.com/en-us/windows/win32/secauthz/privileges
- **unverified.** Whether Administrators hold SeRestore/SeBackup by default and whether SeTakeOwnership lets an admin take ownership and then rewrite the DACL. The privilege table was not readable in this session. Treat as a known Windows fact to confirm on the owner's PC.
- Consequence: no ACL stops a full administrator. This matches doc 46 section 6.6 ("does not defend against a full administrator or root user"). Kodo must state this in its status.

### 3.6 Delete and rename rules (what the installer needs)

- **verified.** "To delete or rename a file, you must have either delete permission on the file or delete child permission in the parent directory." https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexa (Remarks)
- **verified.** `ReplaceFile` opens the replaced file with `GENERIC_READ`, `DELETE`, `SYNCHRONIZE`, and "the caller must have write access to the file to be replaced." It tries `WRITE_DAC` too and falls back if that fails. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
- Consequence: the installer identity needs delete-child on the runtime root and write, delete, and WRITE_DAC on the pointer file, or it must use `REPLACEFILE_IGNORE_ACL_ERRORS`, which "succeeds but the ACLs are not preserved".

### 3.7 Junctions: what they are and their limits

- **verified.** A junction "references ... separate directories", can link directories on different local volumes on the same computer, and "Junctions are implemented through reparse points." https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions
- **verified.** Hard links and junctions: "Hard links can't reference directories." (Hard links only; junctions are the directory form.) Same page.
- **verified.** Reparse points "can be established for a directory, but the directory must be empty. Otherwise, the NTFS file system fails to establish the reparse point." https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points
- **verified.** `mklink /j <link> <target>` creates a directory junction. https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mklink
- **verified.** To remove a symbolic link, "remove the directory (using RemoveDirectory ...) depending on what type of symbolic link is used." https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createsymboliclinka (the page covers symlinks; the same removal call is used for junctions per the mklink example `rd \MyFolder`). The example in the mklink page removes the link with `rd`. https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mklink
- **unverified.** That `RemoveDirectory` on a junction removes only the link and leaves the target contents intact. Not stated in any page read here. Must be tested on hardware before any rollback relies on it.
- **verified.** Creating a symlink needs `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE` (non-elevated) or privilege; the page text read covers the flag, and it does not discuss junctions. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createsymboliclinka
- **unverified.** That junctions need no privilege to create. Commonly known; not stated in the pages read.

### 3.8 MoveFileEx with MOVEFILE_REPLACE_EXISTING on a junction (key finding)

- **verified.** `MOVEFILE_REPLACE_EXISTING`: "If a file named lpNewFileName exists, the function replaces its contents ... If lpNewFileName names an existing directory, an error is reported." https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexa
- **verified.** "When moving a directory, the destination must be on the same drive." Same page.
- Consequence: a junction is a directory (reparse point on a directory). Replacing an existing junction with `MoveFileEx(..., MOVEFILE_REPLACE_EXISTING)` falls under "names an existing directory, an error is reported." **The documented contract does not support replacing the old junction with a rename.** Doc 46 6.3's "same rule" (temp then rename) is therefore not documented as working for junctions.
- **unverified.** Whether `MoveFileEx` or `SetFileInformationByHandle(FileRenameInfo/FileRenameInfoEx)` with replace-if-exists succeeds in practice when the destination is an empty junction. The `FILE_RENAME_INFO` page says `ReplaceIfExists` replaces "the target file" and says nothing about directories. https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info. The `FileRenameInfoEx` reference returned 404 in this session.
- **unverified.** Whether a rename of a junction (as the source) moves only the link and not its target. Not stated in pages read.

### 3.9 Atomicity: no documented primitive for swapping a directory link

- **verified.** The documented atomic-ish file replacement is `ReplaceFile` for a single file. Microsoft's TxF deprecation page recommends `ReplaceFile` for "document-like" single-file updates and says Microsoft "is considering deprecating TxF APIs". https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf
- **verified.** `MoveFileEx` and `ReplaceFile` docs do not claim atomicity across a crash. `ReplaceFile` lists partial-failure return codes (1175, 1176, 1177) where the names can be left in intermediate states. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
- Consequence: no page read promises that a junction swap is atomic on NTFS. The "atomic" property must be a design-level assumption with a test and a recovery path.

### 3.10 Reference-free pointer alternative (documented)

- Doc 46 6.3 also says "One pointer file, `current`, names the active slot." A plain file pointer is the one path where Microsoft documents a replace operation (`ReplaceFile` for files, and `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` for files). See 3.8 and 3.9. So the file-pointer variant is backed by primary docs, and the junction variant is not.

## 4. Proposed mechanics (for the design, not decided)

### 4.1 Slot ACL (to be tested)

Protected DACL on the slot root (inheritance blocked), in this order:

1. Deny ACEs (only if needed). Prefer none, because of 3.2.
2. Allow: the Kodo service identity (a per-service SID or a dedicated group): `FILE_GENERIC_READ | FILE_GENERIC_EXECUTE`. Note that a service can have its own SID (archived Vista page, 3.4 link).
3. Allow: Administrators: read and execute only (matches doc 46 6.2 wording). Admin ownership remains a stated limit (3.5).
4. Allow: the installer identity (a dedicated, signed-update principal): write, create, delete as needed for the staged slot, but only on the staging path, not on the live slot root once it is published.
5. OwnerRights (`S-1-3-4`): an ACE that removes the owner's implicit WRITE_DAC and READ_CONTROL, per 3.4 (archived source; confirm on hardware).
6. No ACE for Users, Authenticated Users, or Everyone that grants any write right. Absence of an allow is the deny.

Inheritance: slot root DACL protected (3.3). Child files and folders inherit from the slot root. Creator-owner inheritable ACEs must be checked, because a child created by the installer takes its owner from the installer token.

### 4.2 Pointer swap options

- **Option A (documented primitives):** `current` is a plain file that names the slot (`slot-a` or `slot-b`), written as `current.tmp` then `ReplaceFile(current, current.tmp, ...)` or `MoveFileEx(current.tmp, current, MOVEFILE_REPLACE_EXISTING)`. Both are documented for files (3.8, 3.10). The launcher reads the name and opens the slot path. Preferred until a junction test passes.
- **Option B (junction, not documented):** temp junction plus rename-over. Blocked by the documented "existing directory" rule in 3.8. Only usable if the hardware run proves the replacement works and that the target and link behave as expected (3.8 unverified items).
- **Option C (no swap in place):** a fixed launcher path. The service host reads `current`, then starts from `slot-X`. Avoids any link object. Same as A in effect.

## 5. Open risks

1. Junction replace is not documented as working (3.8). If the owner wants a junction pointer, that is a finding to reject on the docs unless the hardware run proves otherwise.
2. Admin or SYSTEM can rewrite the slot via ownership or backup privileges (3.4, 3.5). Mitigation is external anchors and sentinel checks (doc 46 6.4 and 6.5), not the ACL.
3. OwnerRights source is archived (3.4). Re-verify on current Windows Server and Windows 11 before any claim is marked verified in a current doc.
4. Open handles: a running process holds slot files, and a rename or delete of an in-use directory or file can fail. Sharing flags and delete-on-close behaviour were not verified here. Needs a test with the actual service host.
5. Installer identity: if the signed update runs as a different principal from the one assumed in 4.1, the write allow must follow that principal. Needs the installer design (doc 37, doc 49) to name it.
6. Crash mid-swap: partial states after a failed replace (3.9) need a recovery rule, such as "pointer file wins, and an orphan `current.tmp` is removed at start".
7. TxF is under deprecation consideration (3.9). Do not use TxF for this design.
8. Linux side (K2) is out of scope here.

## 6. What a hardware run on the owner's PC would need to confirm

Run on a disposable test directory on the owner's Windows PC (not on a live Kodo slot). Each item needs a logged result with the Windows build number.

1. ACL allow-list: create a protected slot root with the ACL in 4.1. Confirm: the service identity can read and execute, Administrators can read, the installer can write, and a standard user cannot write, delete, or change the DACL. Include a test running as Administrators with UAC on and with the filtered token.
2. Inheritance: create a file under the slot from the installer identity, then from a parent-directory write allow. Confirm the protected DACL stops parent ACEs.
3. OwnerRights: with and without the `S-1-3-4` ACE, confirm whether the owner can change the DACL (`WRITE_DAC`).
4. `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` for a file pointer: confirm success and whether it is atomic in practice (readers see either the old or new file, using a concurrent reader loop). Record errors.
5. Junction replace: create `current` as a junction to `slot-a`, then try replacing with a temp junction to `slot-b` using (a) `MoveFileEx` with replace, (b) `SetFileInformationByHandle` `FileRenameInfo` with `ReplaceIfExists`, (c) `FileRenameInfoEx` with replace-if-exists. Record exact error codes. Confirm whether any succeeds without leaving a broken link.
6. Junction removal: `RemoveDirectory` on a junction, with a file inside the target. Confirm the target contents survive.
7. Open handle behaviour: a process holding a file in `slot-a` while `current` moves. Confirm the process keeps running and the old slot can be deleted after the process exits.
8. Crash injection: kill the process between the temp write and the replace, and between the replace and the old-link delete. Confirm the recovery rule (section 5, item 6).
9. Privilege check: confirm whether Administrators hold `SeBackupPrivilege`, `SeRestorePrivilege`, and `SeTakeOwnershipPrivilege` on the owner's build, and whether an elevated admin can change the slot DACL. Record the result as the real boundary.
10. Re-run items 1 to 5 on Windows Server (the owner's chosen host), because the server build is the target, not the desktop build.

Exit criteria for K1 to pass: items 1, 4, 5 (or the decision to use file-pointer only), 6, and 8 produce recorded results, and the design doc 46 section 6.3 is updated to match the proven mechanism in the same change (DECISIONS-LOG if the owner decides).

## 7. Sources (all Microsoft Learn unless noted)

- SetFileAttributes: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileattributesa
- How AccessCheck works (WebFetch read under slug `how-dacls-control-access-to-an-object`): https://learn.microsoft.com/en-us/windows/win32/secauthz/how-dacls-control-access-to-an-object
- Order of ACEs in a DACL: https://learn.microsoft.com/en-us/windows/win32/secauthz/order-of-aces-in-a-dacl
- Automatic propagation of inheritable ACEs: https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces
- Security identifiers (SIDs) New for Windows Vista, OwnerRights (archived): https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-vista/cc749445(v=ws.10)
- Well-known SIDs: https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids
- File security and access rights (backup and restore): https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights
- Privileges: https://learn.microsoft.com/en-us/windows/win32/secauthz/privileges
- MoveFileEx: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexa
- ReplaceFile: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
- FILE_RENAME_INFO: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info
- Hard links and junctions: https://learn.microsoft.com/en-us/windows/win32/fileio/hard-links-and-junctions
- Reparse points: https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points
- CreateSymbolicLink: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createsymboliclinka
- mklink command: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mklink
- Alternatives to TxF: https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf
- Service user accounts (read, no service-SID detail): https://learn.microsoft.com/en-us/windows/win32/services/service-user-accounts
- Service security and access rights (read, not used for a claim): https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights

Pages that returned 404 to WebFetch: the `FileRenameInfoEx` reference (`ns-fileapi-file_rename_info`) and the `access-check` slug. Not cited for any claim.

Related design text: `docs/design/46-kodo-health-module.md` sections 6.2, 6.3, 6.6, 22.
