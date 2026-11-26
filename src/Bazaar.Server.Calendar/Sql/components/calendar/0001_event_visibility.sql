-- Calendar event visibility (docs/design/24 section 4, "Calendar event visibility"). Component migration 0001 in the calendar
-- namespace; it extends calendar_event from common/0017, which the common freeze keeps in place and which runs first.
-- `public` shows the title to everyone, `attendees` to the event's agent and the company owner, `owner` likewise (the table keeps
-- one agent per event). The time block is always shown. Default per the doc: public, except vacations (personal), which are attendees.
ALTER TABLE calendar_event
    ADD COLUMN visibility text NOT NULL DEFAULT 'public' CHECK (visibility IN ('public', 'attendees', 'owner'));

UPDATE calendar_event SET visibility = 'attendees' WHERE kind = 'vacation';
