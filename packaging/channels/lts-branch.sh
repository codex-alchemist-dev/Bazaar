#!/usr/bin/env bash
# Check an LTSC (long-term support) release against its branch (docs/VERSIONING.md section 4,
# docs/design/49-gap-decisions-operations.md section 6). Fails closed.
#
# Usage:
#   lts-branch.sh classify <ref>
#       ref   a branch name (main, ltsc/zuhal, refs/heads/ltsc/zuhal and origin/ltsc/zuhal are accepted)
#       prints kind=ltsc, codename=<name> and major=<N> for an LTSC branch, kind=other otherwise.
#   lts-branch.sh check <branch> <tag> <fork-tag> [ring]
#       branch    the branch the release is cut from; must be an LTSC branch
#       tag       the release tag, vMAJOR.MINOR.PATCH, no pre-release suffix
#       fork-tag  the release tag the LTSC branch was forked at (for example v1.4.0)
#       ring      optional staged ring name, default "all"
#       prints channel=ltsc, ring, line=MAJOR.MINOR, codename and major.
#
# What the docs DECIDE (VERSIONING.md section 4): an LTSC line is a real branch named
# `ltsc/<codename>`, forked at the major's release tag, with fixes cherry-picked; it carries one
# major, security and critical fixes only (doc 49 section 6). What they leave open, so these are
# CONSTANTS in this header and the code below; change them here only, and update the docs when
# the owner decides:
#   LTSC_BRANCH_PREFIX  the branch prefix, from the docs.
#   codename in branch  the lowercase codename (ProductVersion.cs, VERSIONING.md section 2). The
#                       docs do not say whether the branch name is lowercase; lowercase assumed.
#   LTSC tag form       plain vMAJOR.MINOR.PATCH, no pre-release suffix, no `-lts` marker. The
#                       docs name no LTSC tag form. The branch, not the tag, marks the channel,
#                       because tags are immutable SemVer (VERSIONING.md section 4).
#   patch rule          the tag keeps the fork tag's MAJOR.MINOR and has a higher PATCH. This is
#                       the reading of "security and critical fixes only"; the docs do not spell
#                       out the version rule.
# The codename-to-major table below must match src/Bazaar.Contracts/ProductVersion.cs
# (test-lts-branch.sh checks this).
#
# Exit 2 on anything not valid, so release workflows fail closed.

set -euo pipefail

LTSC_BRANCH_PREFIX="ltsc/"

# Lowercase codename -> major. Add a line when a major ships (VERSIONING.md section 2).
codename_major() {
  case "$1" in
    zuhal) echo 1 ;;
    yliaster) echo 2 ;;
    xylokrator) echo 3 ;;
    weltseele) echo 4 ;;
    valthyrzmthl) echo 5 ;;
    *) return 1 ;;
  esac
}

die() { echo "error: $*" >&2; exit 2; }

# Sets codename and major from a branch name, or returns 1 when it is not an LTSC branch.
parse_branch() {
  local b="${1#refs/heads/}"
  b="${b#origin/}"
  [[ "$b" == "$LTSC_BRANCH_PREFIX"* ]] || return 1
  codename="${b#"$LTSC_BRANCH_PREFIX"}"
  [[ "$codename" =~ ^[a-z]+$ ]] || return 1
  major="$(codename_major "$codename")" || return 1
}

# Release tag with no pre-release suffix; sets p_major, p_minor, p_patch.
parse_tag() {
  local re='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
  [[ "$1" =~ $re ]] || return 1
  p_major="${BASH_REMATCH[1]}"; p_minor="${BASH_REMATCH[2]}"; p_patch="${BASH_REMATCH[3]}"
}

mode="${1:-}"
case "$mode" in
  classify)
    ref="${2:-}"
    if parse_branch "$ref"; then
      printf 'kind=ltsc\ncodename=%s\nmajor=%s\n' "$codename" "$major"
    elif [[ "${ref#refs/heads/}" == "$LTSC_BRANCH_PREFIX"* ]]; then
      die "'$ref' looks like an LTSC branch but is not ${LTSC_BRANCH_PREFIX}<known codename>."
    else
      echo "kind=other"
    fi
    ;;
  check)
    branch="${2:-}"; tag="${3:-}"; fork="${4:-}"; ring="${5:-all}"
    parse_branch "$branch" || die "'$branch' is not an LTSC branch (expected ${LTSC_BRANCH_PREFIX}<codename>); an LTSC release may only be cut from one."
    [[ "$ring" =~ ^[a-z][a-z0-9-]{0,31}$ ]] || die "ring '$ring' must be 1-32 lowercase letters, digits or hyphens, starting with a letter."
    parse_tag "$tag" || die "'$tag' is not an LTSC release tag (expected vMAJOR.MINOR.PATCH with no pre-release suffix)."
    tag_major="$p_major"; tag_minor="$p_minor"; tag_patch="$p_patch"
    parse_tag "$fork" || die "fork tag '$fork' is not vMAJOR.MINOR.PATCH with no pre-release suffix."
    [ "$p_major" = "$major" ] || die "fork tag '$fork' has major $p_major, but branch $branch is major $major."
    [ "$tag_major" = "$major" ] || die "tag '$tag' has major $tag_major, but branch $branch is major $major."
    [ "$tag_minor" = "$p_minor" ] || die "tag '$tag' changes MINOR ($tag_minor, line is $major.$p_minor). An LTSC line takes patch-only bumps."
    [ "$tag_patch" -gt "$p_patch" ] || die "tag '$tag' must have a PATCH above the fork tag '$fork'."
    printf 'channel=ltsc\nring=%s\nline=%s.%s\ncodename=%s\nmajor=%s\n' "$ring" "$major" "$p_minor" "$codename" "$major"
    ;;
  *)
    die "usage: lts-branch.sh classify <ref> | check <branch> <tag> <fork-tag> [ring]"
    ;;
esac
