-- P2 to P3, per GC, read from the system entry. One line per GC; all checks must be t.
with rec as (
    select m.gc_id,
           m.seq as rec_seq,
           (m.body::json ->> 'lost_from')::bigint as lost_from,
           (m.body::json ->> 'lost_to')::bigint as lost_to
    from gc_message m
    where m.kind = 'system'
)
select r.gc_id,
       r.lost_from,
       r.lost_to,
       r.rec_seq,
       (select count(*) = r.lost_from - 1
          from gc_message m where m.gc_id = r.gc_id and m.seq < r.lost_from) as prefix_contiguous,
       (select count(*) = 0
          from gc_message m where m.gc_id = r.gc_id and m.seq between r.lost_from and r.lost_to) as lost_range_absent,
       (select count(*) = max(m.seq) - r.rec_seq + 1
          from gc_message m where m.gc_id = r.gc_id and m.seq >= r.rec_seq) as suffix_contiguous,
       (r.rec_seq = r.lost_to + 1) as entry_at_floor,
       ((select g.head_seq from gc g where g.gc_id = r.gc_id) = r.rec_seq) as head_matches
from rec r
order by r.gc_id;
