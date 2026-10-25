#!/bin/sh
# archive_command helper: archive_command = 'sh /spike/code/archive.sh %p %f'
# Copies to a temp name, fsyncs, checks the size, then renames into place.
# Exits non-zero on any doubt so PostgreSQL keeps the segment and retries.
set -eu
src="$1"
name="$2"
dir=/spike/work/wal
dst="$dir/$name"
tmp="$dir/.$name.tmp"

if [ -e "$dst" ]; then
    # Already archived: succeed only when the copy is identical.
    cmp -s "$src" "$dst"
    exit $?
fi

cp "$src" "$tmp"
sync "$tmp"
[ "$(stat -c %s "$src")" = "$(stat -c %s "$tmp")" ]
mv "$tmp" "$dst"
