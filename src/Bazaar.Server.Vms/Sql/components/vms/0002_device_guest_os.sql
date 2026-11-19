-- Device guest OS values (docs/design/62-device-vm-providers-android-ios-macos.md section 4). Additive: 'android' joins the allowed os values.
-- iOS and macOS are parked (owner card, 2026-11-19) and are not added here.
ALTER TABLE golden_image DROP CONSTRAINT golden_image_os_check;
ALTER TABLE golden_image ADD CONSTRAINT golden_image_os_check CHECK (os IN ('windows', 'linux', 'android'));
ALTER TABLE environment_vm DROP CONSTRAINT environment_vm_os_check;
ALTER TABLE environment_vm ADD CONSTRAINT environment_vm_os_check CHECK (os IN ('windows', 'linux', 'android'));
