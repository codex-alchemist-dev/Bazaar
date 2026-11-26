-- Apple guest OS values (docs/design/62-device-vm-providers-android-ios-macos.md sections 5.2, 5.3 and 6). Additive: 'ios' and 'macos' join the allowed os values.
-- The rows exist only when the optional iOS or macOS provider module is installed and enabled, and run on a Mac node (Apple hardware only).
ALTER TABLE golden_image DROP CONSTRAINT golden_image_os_check;
ALTER TABLE golden_image ADD CONSTRAINT golden_image_os_check CHECK (os IN ('windows', 'linux', 'android', 'ios', 'macos'));
ALTER TABLE environment_vm DROP CONSTRAINT environment_vm_os_check;
ALTER TABLE environment_vm ADD CONSTRAINT environment_vm_os_check CHECK (os IN ('windows', 'linux', 'android', 'ios', 'macos'));
