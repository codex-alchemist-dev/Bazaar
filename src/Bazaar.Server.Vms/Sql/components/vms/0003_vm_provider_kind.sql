-- Provider kind of each VM (docs/design/62-device-vm-providers-android-ios-macos.md section 9.2). Additive and nullable: a row written
-- before routing existed keeps null and routes by the legacy rule (the pair provider, else the single provider for its OS).
ALTER TABLE environment_vm ADD COLUMN provider_kind text
    CHECK (provider_kind IS NULL OR provider_kind ~ '^[a-z][a-z0-9_]{0,63}$');
