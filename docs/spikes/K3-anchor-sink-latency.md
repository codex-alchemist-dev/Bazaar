# Spike K3: anchor sink latency and object lock support

Branch: none (research only, no code). Design source: `docs/design/46-kodo-health-module.md` section 22 (K3), section 6.5 (journal anchoring), section 2 (audit anchoring), section 20 (`kodo.journal.anchor_sinks`), section 23 question 5.
Status: RESEARCHED (not measured). Every figure below comes from a cited source or is marked as not stated. No latency was measured in this spike.

## Question

For a Kodo journal checkpoint (one checkpoint per window of 1,000 records or 15 minutes, whichever comes first, per doc 46 section 6.5), which external anchor sink gives a write-once guarantee and what does it cost in latency? The sinks in scope are the three that doc 46 names: an S3-compatible object store with object lock, an RFC 3161 timestamp authority (TSA), and a public transparency log. The owner's paired devices (the default in doc 46 section 20) were not researched.

## Method

- Read doc 46 sections 2, 5, 6.5, 20, 22, 23 (local file, primary).
- Read the design-doc spike template, `docs/spikes/_TEMPLATE.md`, and sibling spike records for format.
- Web research, primary vendor and standards pages first, read through a summarising fetch (so quotes are the fetch's extraction, not byte-exact copies). Secondary sources (search snippets, third-party blogs) are labelled as such.
- Dates: research as of 2026-11-13. Some vendor pages gave dates in 2026 (noted where relevant).
- No latency measurements were taken. No accounts were created. No endpoint was called.

Labels: **verified** = primary source read; **secondary** = third-party or search snippet; **unverified** = claim seen only in a secondary source, or not checkable.

## Options table

"Not stated" means the source read did not give a figure. It does not mean the figure is zero or small.

| # | Option | Type | Write or inclusion latency | Cost class | Lock guarantee | Sources (label) |
|---|---|---|---|---|---|---|
| 1 | AWS S3 Object Lock, compliance mode | S3 API, hosted | Not stated | Paid. Storage and request charges. Pricing page does not mention Object Lock (no separate charge stated) | No user, including root, can overwrite or delete a version before its retain-until date. Only way to remove early is to delete the AWS account. Retention cannot be shortened | [Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) (verified); [pricing](https://aws.amazon.com/s3/pricing/) (verified, silent on Object Lock) |
| 2 | AWS S3 Object Lock, governance mode | S3 API, hosted | Not stated | Paid, as above | Protected from most users. Users with `s3:BypassGovernanceRetention` plus the bypass header can delete or shorten | [Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) (verified) |
| 3 | Backblaze B2 Object Lock | S3-compatible and native API, hosted | Not stated | Object Lock: "no extra cost", standard storage charges apply | Governance: modifiable by clients with app key capabilities. Compliance: "cannot be removed by any user", retention can be extended. Retention 1 to 3,000 days. Legal hold separate | [Backblaze Object Lock](https://www.backblaze.com/docs/cloud-storage-object-lock) (verified, fetch summary; page quotes) |
| 4 | Azure Blob immutable storage (time-based retention and legal hold). Not an S3 API | Azure hosted | Not stated | "No extra capacity charge"; write transactions on policy creation and deletion, and "Other operations" charges on lock or extend | Locked policy cannot be deleted or reduced, only extended. Unlocked policy can be shortened or deleted (testing only). Legal hold until cleared. Min 1 day, max 146,000 days | [Azure immutable storage](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview) (verified) |
| 5 | Google Cloud Bucket Lock (retention policy). Not the S3 Object Lock API | GCS hosted | Not stated | Not stated | Locking is irreversible: "you cannot remove it or reduce the retention period". Locked bucket cannot be deleted until objects expire. Locking places a lien on the project. Event-based holds | [GCS Bucket Lock](https://docs.cloud.google.com/storage/docs/bucket-lock) (verified, fetch redirected from cloud.google.com) |
| 6 | Cloudflare R2 bucket locks | R2 hosted | Not stated | Not stated (page has no pricing) | Prefix rules, duration or indefinite. Rules "can be removed" from the dashboard, with Wrangler, or by omitting them from an API update. Page makes no S3 Object Lock compatibility claim. Weaker than compliance mode as documented | [R2 bucket locks](https://developers.cloudflare.com/r2/buckets/bucket-locks/) (verified) |
| 7 | Wasabi Object Lock | S3 API, hosted | Not stated | Not stated (no pricing read) | Governance is the default mode. Compliance mode also exists. Object lock can be enabled only at bucket creation. Mutual exclusivity with Wasabi's separate Compliance feature is secondary only | [Wasabi enable page](https://docs.wasabi.com/docs/object-locking-enable) (verified for default mode and creation-time rule); mutual exclusivity from [search snippet](https://docs.wasabi.com/docs/object-locking-enable.md) (secondary, unverified) |
| 8 | MinIO AIStor object lock (commercial product). Community Edition support unclear | Self-hosted S3 API | Not stated | Commercial (AIStor). Community Edition status: unverified | Compliance: "No MinIO AIStor user can modify the object ... including the MinIO AIStor root user." Governance bypassable with permission and header. Legal hold. Versioning required. Enable at creation, or on existing bucket via AIStor client | [MinIO AIStor object locking](https://docs.min.io/enterprise/aistor-object-store/administration/object-locking-and-immutability/) (verified, fetch redirected from min.io). Community status: [secondary search results](https://glukhov.org/data-infrastructure/object-storage/minio-dead/) (secondary, unverified) |
| 9 | Ceph RGW S3 Object Lock | Self-hosted, open source | Not stated | Open source, operator cost only | Secondary sources say governance and compliance modes are supported, with legal hold. One Red Hat bug report says GOVERNANCE can be switched to COMPLIANCE without a bypass check. Official Ceph doc not read (404 on the URL tried) | [Ceph object lock doc](https://docs.ceph.com/en/latest/radosgw/s3/objectlock/) (not read, 404); [secondary guides](https://oneuptime.com/blog/post/2026-03-31-rook-set-up-ceph-object-lock-worm-compliance/markdown) (secondary, unverified); [Red Hat bug 1890843](https://bugzilla.redhat.com/show_bug.cgi?id=1890843) (secondary) |
| 10 | RFC 3161 TSA, FreeTSA.org | Standard protocol, public TSA | Not stated. RFC sets no target | Free (per the page). Usage policy not read | Not a write lock. Proves a hash existed at `genTime`. Token signed by TSA. Hash algorithms SHA-1 to SHA-512. No SLA, no rate limit stated. Old TSA cert expired March 2026; new one valid to February 2040 | [FreeTSA](https://www.freetsa.org/index_en.php) (verified, fetch summary); [RFC 3161](https://www.rfc-editor.org/rfc/rfc3161) (verified, fetch summary) |
| 11 | RFC 3161 TSA, DigiCert public endpoint `http://timestamp.digicert.com` | Standard protocol, commercial TSA | Not stated | Not stated. Free status conflicting: a community post says no longer free (undated, unverified). No terms for the endpoint found | Not a write lock. Same proof-of-existence property. No SLA or response figure on the KB page. Certificates replaced 3 September 2026 per KB | [DigiCert KB](https://knowledge.digicert.com/general-information/rfc3161-compliant-time-stamp-authority-server) (verified, fetch summary) |
| 12 | OpenTimestamps (aggregation into Bitcoin) | Public calendars, anchored in Bitcoin | Calendar receipt "about a second", incomplete. Bitcoin confirmation "at least 10 minutes on average", and "a few hours" to confirm | Free | Not a write lock. Proof of existence, verifiable without the service. Confirmation depends on Bitcoin | [OpenTimestamps announcement](https://petertodd.org/2016/opentimestamps-announcement) (verified, fetch summary; author's post, not the spec) |
| 13 | Public transparency log, Rekor (Sigstore) | Public append-only log | Not stated. Availability SLO 99.5% stated | Cost not stated | Append-only, entries "never mutated or removed" per the page. Inclusion proofs via CLI. Signed tree heads not mentioned on the page | [Sigstore Rekor](https://docs.sigstore.dev/logging/overview/) (verified, fetch summary) |
| 14 | Certificate Transparency RFC 6962 log (as a bound on merge time) | Public log class | Max Merge Delay of 4 hours for RFC 6962 logs; 1 minute for static-ct logs (Chrome policy maximums) | Operator-run, not a service to buy | Append-only; inclusion within MMD, promised by SCT | [Chrome CT log policy](https://googlechrome.github.io/CertificateTransparency/log_policy.html) (verified, fetch summary); [RFC 9162](https://www.rfc-editor.org/rfc/rfc9162.html) (verified, fetch summary) |
| 15 | Owner's paired devices (doc 46 default) | Not researched | Not researched | Not researched | Not researched | Doc 46 section 6.5 and section 20 only |

## Findings

### F1. Object lock: the two modes differ on who can bypass, and that matters for the anchor
- Compliance mode: no user can overwrite or delete, including the root user; retention cannot be shortened. The only early delete is deleting the AWS account. [AWS Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) (verified).
- Governance mode: users with `s3:BypassGovernanceRetention` and the explicit header can delete or shorten. Same doc (verified).
- For a Kodo anchor, compliance mode is the one that matches "rewriting history ... including by an operator with database access" (doc 46 section 6.6 item 4). Governance mode leaves the anchor exposed to any operator who holds the bypass permission, which may be the same person as the server operator.

### F2. Object lock needs a versioned bucket, and locks protect versions, not keys
- AWS: Object Lock works only with versioning; locks protect the version specified; new versions and delete markers are still allowed; delete markers are not WORM-protected. [AWS Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) (verified).
- MinIO AIStor: versioning is required; "Creating a locked bucket also turns on versioning." [MinIO AIStor](https://docs.min.io/enterprise/aistor-object-store/administration/object-locking-and-immutability/) (verified).
- Implication: the checkpoint object key should be unique per checkpoint, and the verifier must check the version id, not only the key.

### F3. Creation-time versus later enablement varies by provider
- AWS: enable on new bucket or existing bucket; cannot be disabled after; versioning cannot be suspended after enabling. [AWS configure](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-configure.html) and [AWS managing](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html) (verified).
- Backblaze: can be enabled on an existing bucket; cannot be disabled after. [Backblaze](https://www.backblaze.com/docs/cloud-storage-object-lock) (verified).
- Wasabi: enablement "when the bucket was created" (verified from the enable page). Choose the bucket at creation.
- MinIO AIStor: enable at creation, or on an existing bucket through the AIStor client from a named release. [MinIO AIStor](https://docs.min.io/enterprise/aistor-object-store/administration/object-locking-and-immutability/) (verified).

### F4. Object store upload needs a checksum header
- AWS: uploads with an Object Lock retention need `Content-MD5` or `x-amz-sdk-checksum-algorithm` (or both). [AWS managing](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html) (verified). This is a write rule, not a latency figure.

### F5. Retention limits differ and must fit doc 46 settings
- AWS bucket policy maximum: 100 years (verified, [AWS managing](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html)).
- Backblaze: 1 to 3,000 days (verified).
- Azure: 1 to 146,000 days (verified).
- Doc 46 settings: journal 730 days, evidence 2,555 days (doc 46 section 20). The Backblaze maximum of 3,000 days covers both. Note that 2,555 days is under 3,000, so it fits, but a longer retention would not. [doc 46 section 20](file:///home/user/Bazaar/docs/design/46-kodo-health-module.md) (local file, verified).

### F6. Lock guarantees depend on the product, and several "locks" are removable
- Cloudflare R2 documents removal of lock rules through the dashboard, Wrangler, or an API update. Its lock is weaker than compliance mode as documented (verified).
- Google Cloud locks cannot be reduced once locked, but the project-level lien and the bucket-deletion rule apply (verified).
- Azure: an unlocked time-based policy can be deleted; a locked one cannot (verified).
- Compare the claim in doc 46 section 6.6 that Kodo does not defend against a full administrator who controls keys, sinks and devices at once. The object-lock layer is only as strong as the account that owns the bucket. A compliance lock held in an account the same operator controls is still an operator-controlled sink.

### F7. Latency: no source gives an object-store or TSA response time
- No source read gives a PUT, object-lock, or retention-set latency figure for S3, Backblaze, Azure, GCS, R2 or Wasabi. The figures in the table are "not stated".
- RFC 3161 gives no latency or throughput target. It says "any TimeStampResp that takes more than an acceptable period of time SHOULD be considered suspect" (verified, [RFC 3161](https://www.rfc-editor.org/rfc/rfc3161) section 4).
- A search snippet quotes network latency of 100 to 500 ms for a signing tool; it is a general estimate, not a TSA benchmark, and not a primary source (secondary, unverified). Not used as a figure.
- Public transparency logs: Chrome's policy caps MMD at 4 hours for RFC 6962 logs and 1 minute for static-ct logs (verified). These are maximums, not measured inclusion times.
- OpenTimestamps: "about a second" for a calendar receipt, but incomplete until Bitcoin confirms; Bitcoin confirmation "at least 10 minutes on average" and "a few hours" (verified, author's announcement; the spec or current calendar behaviour not read).

### F8. How a checkpoint is anchored in each case (design analysis, not a source fact)
Let `R` be the Merkle root, `H` the chain head, and `W` the window range. The checkpoint body is `{schema, window, H, R, W, prev checkpoint id}`, signed with the journal key (doc 46 section 6.5).

- Object lock sink: store the signed checkpoint body as one object, key = checkpoint id, compliance-mode retention from the bucket default or per object. Anchor = the object version id plus its retention, both written to the journal. Time claim = the provider's own record of the write. The provider's time is not signed as an RFC 3161 token would be, so a verifier trusts the provider's record.
- RFC 3161 sink: send `SHA-256(signed checkpoint body)` as the message imprint (RFC 3161 says the TSA should only timestamp a hash, and "messageImprint ... SHOULD contain the hash of the datum", verified). Store the returned TimeStampToken, which is a signed TSTInfo with `genTime` and accuracy, in the journal and the checkpoint object. Request a nonce to detect replay (verified in RFC 3161). Offline check possible with the TSA certificate.
- Public log sink: submit `SHA-256(R)` (or the signed checkpoint) as an entry; store the inclusion proof and the signed tree head. Rekor's exact entry type and the CT-style tree head format were not read in detail.
- OpenTimestamps sink: submit `SHA-256(signed checkpoint)`; store the pending receipt; upgrade to a Bitcoin-anchored proof later. Pending state must be shown as pending, never as complete (analysis of the calendar behaviour above).
- Volume (derived arithmetic, not sourced): a 15-minute window gives at most 96 windows per day; the 1,000-record rule can close windows earlier at high write rates. So anchor calls per day are between 1 and roughly 96 per window clock plus record-driven closes. Actual counts depend on load, which is not measured.

### F9. Cost classes (summary)
- Free: FreeTSA (per page), OpenTimestamps (per page). Rekor cost not stated. DigiCert endpoint: unclear (unverified).
- Paid, storage and requests: AWS, Backblaze (no extra lock fee), Azure (no extra capacity charge, transaction charges on policy ops), GCS (not stated), R2 (not stated), Wasabi (not stated).
- Self-hosted: Ceph (open source, operator cost), MinIO (commercial AIStor; Community status unverified).

## Open risks

1. No latency number exists in any source read. The checkpoint window of 15 minutes is a design choice, not a measured fit. Anchor latency must be measured before doc 46 sets an anchor deadline.
2. Object lock guarantees depend on the bucket owner. A bucket inside the operator's own account is weaker than one held in a separately controlled account. Doc 46 section 23 question 5 needs this distinction.
3. Governance mode is bypassable by design. Using it as the Kodo default would contradict doc 46's rewrite-proof goal. Compliance mode is required for the default, but compliance mode also means the retention cannot be shortened if the install is mis-set.
4. Compliance mode prevents deletion by the operator. Storage cost grows for the full retention; doc 46 retention settings (730 and 2,555 days) multiply by checkpoint count.
5. Free TSA endpoints carry no stated SLA, rate limit or usage terms that were read. Using a free public TSA as a default would rely on an unmeasured service. DigiCert's free status is unresolved.
6. MinIO Community Edition status and Ceph object-lock support rest on secondary sources. Do not rely on either as a self-hosted option until the official docs and the release notes are read.
7. The RFC 3161 `genTime` is the TSA's clock. Accuracy values are per TSA policy, not stated in the FreeTSA read. Accuracy must be read from each TSA's policy before a time claim is made.
8. Cloudflare R2 rules are removable, so it does not meet the write-once guarantee as documented.
9. Conflict with the owner decision on Apache versus AGPL does not apply here. No licence conflict found in the sinks researched.
10. Doc 46 section 6.5 names the object-lock sink as "S3-compatible". Backblaze says its lock "works the same way as it does with AWS" (verified, vendor claim). Other S3-compatible services were not checked for lock behaviour.

## What a measurement run would need

Per sink, run the same checkpoint workload and record the numbers. Do not report a median without the spread. Three runs minimum, following the spike template.

- Environment: the Windows host (the owner's PC) and a Linux host, with the .NET 11 SDK from `global.json`, the kernel and network path, and a timestamp of each run. Record CPU, RAM and disk.
- Workload: 1,000 synthetic records per window and a 15-minute window, plus a burst at 10 times the window rate, and an idle case with no records (a 15-minute window still closes, per doc 46 section 6.5).
- Per sink:
  - Object lock: S3 API PUT with checksum header and compliance retention, time to 200 OK, time to a GET of the version with retention metadata, and an attempted delete (must fail) and a bypass attempt (must fail for compliance). Use a test bucket in a separate account. Record the provider name and region.
  - RFC 3161: POST a TimeStampReq with a nonce, time to TimeStampResp, token verification against the TSA certificate chain, and `genTime` against the local clock. Repeat at off-peak and peak. Record the TSA name, policy OID and the accuracy field.
  - Public log: submit, time to inclusion, and an inclusion proof check. Record the MMD the log declares.
  - OpenTimestamps: submit, time to a calendar receipt, time to a Bitcoin-confirmed upgrade (wall clock, block height).
- Failure cases: sink unreachable (breaker open, doc 46 section 7.4), timeout, bad response, and the journal spill path (doc 46 section 7.5) with a checkpoint queued while the sink is down.
- Pass criteria to fix before the first run (copy from the spikes plan `docs/design/35-spikes-plan.md`, not read in this spike): the time-box and the pass criteria must come from that file, not this note.
- Outputs for the main session: a DECISIONS-LOG entry on the default sink and its retention mode, the cost per month at the 730-day and 2,555-day retention settings, and whether the 15-minute window holds under measured latency.
- Owner questions to answer before measuring: which account owns the object-lock bucket, which TSA (if any) is allowed under the owner's cost rules, and whether a public log may receive an installation's checkpoint hash (privacy and metadata exposure; doc 46 section 6.5 says the public transparency log is an optional sink).

## Sources

- Doc 46, local: `/home/user/Bazaar/docs/design/46-kodo-health-module.md` (sections 2, 5, 6.5, 6.6, 7.4, 7.5, 20, 22, 23) (verified, read)
- Spike template, local: `/home/user/Bazaar/docs/spikes/_TEMPLATE.md` (verified, read)
- [AWS S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html) (verified)
- [AWS S3 Object Lock configure](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-configure.html) (verified)
- [AWS S3 Object Lock considerations](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html) (verified)
- [AWS S3 pricing](https://aws.amazon.com/s3/pricing/) (verified; silent on Object Lock)
- [Backblaze B2 Object Lock](https://www.backblaze.com/docs/cloud-storage-object-lock) (verified)
- [Azure immutable storage](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview) (verified)
- [Google Cloud Bucket Lock](https://docs.cloud.google.com/storage/docs/bucket-lock) (verified)
- [Cloudflare R2 bucket locks](https://developers.cloudflare.com/r2/buckets/bucket-locks/) (verified)
- [Wasabi object locking enable](https://docs.wasabi.com/docs/object-locking-enable) (verified for default and creation-time rule)
- [MinIO AIStor object locking](https://docs.min.io/enterprise/aistor-object-store/administration/object-locking-and-immutability/) (verified)
- [MinIO CE status, secondary](https://glukhov.org/data-infrastructure/object-storage/minio-dead/) (secondary, unverified)
- [Ceph RGW object lock, not read (404)](https://docs.ceph.com/en/latest/radosgw/s3/objectlock/) (not read)
- [Ceph secondary guides](https://oneuptime.com/blog/post/2026-03-31-rook-set-up-ceph-object-lock-worm-compliance/markdown) (secondary, unverified)
- [Red Hat bug 1890843](https://bugzilla.redhat.com/show_bug.cgi?id=1890843) (secondary)
- [RFC 3161](https://www.rfc-editor.org/rfc/rfc3161) (verified, fetch summary)
- [FreeTSA](https://www.freetsa.org/index_en.php) (verified, fetch summary)
- [DigiCert RFC 3161 KB](https://knowledge.digicert.com/general-information/rfc3161-compliant-time-stamp-authority-server) (verified, fetch summary)
- [DigiCert search snippet on free status](https://gist.github.com/fd754e402d98430243455713efada710) (secondary, unverified; community post, undated)
- [OpenTimestamps announcement](https://petertodd.org/2016/opentimestamps-announcement) (verified, fetch summary; author's post)
- [Sigstore Rekor overview](https://docs.sigstore.dev/logging/overview/) (verified, fetch summary)
- [RFC 9162 Certificate Transparency 2.0](https://www.rfc-editor.org/rfc/rfc9162.html) (verified, fetch summary)
- [Chrome CT log policy](https://googlechrome.github.io/CertificateTransparency/log_policy.html) (verified, fetch summary)

## Not verified

- Any latency or response-time figure for any object store, TSA or log. None was in a source read.
- Object-lock PUT latency and retention-set latency for any provider.
- Pricing for Wasabi, Cloudflare R2, Google Cloud Bucket Lock and the DigiCert endpoint.
- Whether the DigiCert public RFC 3161 endpoint is free and its usage terms.
- FreeTSA's usage policy text and its accuracy field.
- MinIO Community Edition object-lock support in 2026 (official check of the repository and release notes still needed).
- Ceph RGW object-lock behaviour from official docs (the official page returned 404).
- Wasabi's "compliance and object lock are mutually exclusive" claim (secondary only).
- Rekor's entry format, signed tree head and cost.
- The owner's paired-device sink (not researched).
- The spike time-box and pass criteria from `docs/design/35-spikes-plan.md` (not read in this spike).
