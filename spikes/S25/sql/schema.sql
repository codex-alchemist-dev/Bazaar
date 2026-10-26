-- S25 synthetic GC log. Shape follows docs 01 and 04 only as far as the drill needs:
-- per-GC sequence, hash partitions by gc_id, a system entry kind.
create table gc (
    gc_id      int primary key,
    head_seq   bigint not null default 0
);

create table gc_message (
    gc_id       int not null references gc (gc_id),
    seq         bigint not null,
    kind        text not null default 'message' check (kind in ('message', 'system')),
    body        text not null,
    created_at  timestamptz not null default clock_timestamp(),
    primary key (gc_id, seq)
) partition by hash (gc_id);

create table gc_message_p0 partition of gc_message for values with (modulus 4, remainder 0);
create table gc_message_p1 partition of gc_message for values with (modulus 4, remainder 1);
create table gc_message_p2 partition of gc_message for values with (modulus 4, remainder 2);
create table gc_message_p3 partition of gc_message for values with (modulus 4, remainder 3);

insert into gc (gc_id) values (1), (2), (3), (4);
