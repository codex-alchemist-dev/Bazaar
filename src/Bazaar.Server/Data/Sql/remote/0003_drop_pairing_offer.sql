-- 0003 (component remote): removes device pairing (owner, 2026-11-16).
-- Every Bazaar client is built by its company's server and bound to it, so no pairing offer or device request exists.
-- Doc 32 section 3 is removed. Devices, refresh credentials, humans and sign-in tables from 0001 stay.
-- A new migration is added here rather than editing 0001 or 0002, which may already be applied.

DROP TABLE pairing_offer;
