-- 0002 (component remote): the device request an offer carries once a device starts authorization (doc 32 section 3).
-- The approver's Approvals panel shows the label and class, so they are stored with the offer. last_polled_at lets the
-- server answer slow_down (RFC 8628) when a device polls faster than its interval.
-- Owner: the pairing, tokens and authorization thread (group 32). Inert until the component model loads components/.

ALTER TABLE pairing_offer
    ADD COLUMN device_label text CHECK (device_label IS NULL OR length(device_label) BETWEEN 1 AND 200),
    ADD COLUMN device_class text CHECK (device_class IS NULL OR device_class IN ('windows', 'linux', 'android', 'terminal', 'other')),
    ADD COLUMN last_polled_at timestamptz;
