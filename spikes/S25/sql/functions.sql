-- Append n messages to one GC. Sequence numbers are assigned under the GC row lock.
create or replace function append_messages(p_gc int, p_n int) returns void
language plpgsql as $$
declare
    v_last bigint;
begin
    update gc set head_seq = head_seq + p_n where gc_id = p_gc returning head_seq into v_last;
    insert into gc_message (gc_id, seq, body)
    select p_gc, v_last - p_n + g, 'msg ' || (v_last - p_n + g)
    from generate_series(1, p_n) as g;
end $$;

-- Rollback floor for one GC (doc 33): the first seq after the highest seq any surviving
-- source saw becomes a system entry that records the lost range. Nothing is reconstructed.
create or replace function apply_recovery(p_gc int, p_seen bigint, p_restored_at timestamptz)
returns bigint language plpgsql as $$
declare
    v_head bigint;
begin
    select head_seq into v_head from gc where gc_id = p_gc for update;
    if p_seen < v_head then
        raise exception 'surviving source saw seq %, below restored head %', p_seen, v_head;
    end if;
    insert into gc_message (gc_id, seq, kind, body)
    values (p_gc, p_seen + 1, 'system',
            format('{"type":"restore_recovery","lost_from":%s,"lost_to":%s,"restored_at":"%s"}',
                   v_head + 1, p_seen, p_restored_at));
    update gc set head_seq = p_seen + 1 where gc_id = p_gc;
    return p_seen + 1;
end $$;
