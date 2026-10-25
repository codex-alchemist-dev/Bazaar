-- One line per GC: gc_id, row count, max seq, head_seq. Run with -At -F,
select g.gc_id,
       count(m.seq),
       coalesce(max(m.seq), 0),
       g.head_seq
from gc g
left join gc_message m on m.gc_id = g.gc_id
group by g.gc_id, g.head_seq
order by g.gc_id;
