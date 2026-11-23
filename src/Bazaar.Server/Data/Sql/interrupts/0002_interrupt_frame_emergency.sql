-- The owner's `emergency` tag is stored on the frame (doc 24 section 3, C35) so the runtime and schedule can read it after the
-- push, not only in the push answer. Existing frames were pushed without the tag.
ALTER TABLE interrupt_frame
    ADD COLUMN emergency boolean NOT NULL DEFAULT false;
