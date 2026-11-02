# Red-team known gaps

Gaps found by the red-team replay (WP12, doc 15 "Testing"). Each gap has a skipped test that names it. Production code
in `src/` is not changed by the corpus work; the gap is recorded here for a later work package.

## RT-1: the sanitizer does not handle invisible or bidi-control characters

- **Where:** `src/Bazaar.Server/Sanitization/TextSanitizer.cs`. Its fixed rules cover secrets, Vault paths and links, emails
  and quoted text. None covers Unicode format characters (category Cf): zero-width space and joiners (U+200B to U+200D),
  word joiner (U+2060), byte-order mark (U+FEFF), soft hyphen (U+00AD), bidi overrides and isolates (U+202A to U+202E,
  U+2066 to U+2069), and tag characters (U+E0000 to U+E007F). Nothing in `src/` marks or strips them.
- **Effect on the replay:** every `hidden_unicode` case still gets its Tier 2 and Tier 3 calls refused, because the
  content is external and taints the session hard. The gap is in what reaches a reader or a human reviewer. A hidden
  instruction in text that is not external (for example agent or member text) passes the sanitizer unchanged.
- **Skipped test:** `RedTeamSanitizerTests.Sanitizer_removes_invisible_and_bidi_controls_from_hidden_unicode_cases`.
  Confirmed to fail when unskipped: all four `hu-` cases keep their format characters.
- **Suggested fix (not done here):** strip Cf characters in `TextSanitizer` with a `RedactionKind` entry, or flag and
  quarantine content that contains them, then remove the skip.
