using System.Diagnostics;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm.Tests;

/// <summary>Drives a real QEMU (TCG, no display) through the unix-socket transport. Skipped when QEMU is not installed.</summary>
public class RealQemuTests
{
    private const string _qemuPath = "/usr/bin/qemu-system-x86_64";

    [Fact]
    public async Task Capture_and_input_work_against_a_real_qemu()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(_qemuPath))
            Assert.Skip($"{_qemuPath} is not installed, so the real QEMU test cannot run.");

        string dir = Path.Combine(Path.GetTempPath(), "bzr-qemu-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string socket = Path.Combine(dir, "qmp.sock");
        string dumps = Path.Combine(dir, "dumps");
        Directory.CreateDirectory(dumps);

        var psi = new ProcessStartInfo(_qemuPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-accel", "tcg", "-m", "128", "-display", "none", "-vga", "std", "-device", "usb-ehci", "-device", "usb-tablet", "-qmp", $"unix:{socket},server,nowait" })
            psi.ArgumentList.Add(a);

        using Process qemu = Process.Start(psi)!;
        try
        {
            for (int i = 0; i < 100 && !File.Exists(socket) && !qemu.HasExited; i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(socket), "QEMU did not create its QMP socket.");

            var options = new KvmOptions { ScreendumpDirectory = dumps, CommandTimeoutSeconds = 10 };
            var transport = new UnixSocketQmpTransport(_ => socket, TimeSpan.FromSeconds(10));
            var console = new KvmConsole(options, transport, new LocalScreendumpFileReader(options.MaxScreendumpBytes));
            DisplayTarget target = Targets.For("realvm");
            CancellationToken ct = TestContext.Current.CancellationToken;

            // The VGA BIOS needs a moment before the first frame exists.
            RawFrame? frame = null;
            for (int i = 0; i < 20 && frame is null; i++)
            {
                try
                {
                    frame = await console.ReadFramebufferAsync(target, 1920, 1200, ct);
                }
                catch (DisplayException)
                {
                    await Task.Delay(250, ct);
                }
            }

            Assert.NotNull(frame);
            Assert.Equal(RawPixelFormat.Png, frame.Format);
            Assert.True(ImageHeader.IsPng(frame.Data), "The capture is not a PNG.");
            Assert.True(ImageHeader.TryReadPng(frame.Data, out int w, out int h));
            Assert.True(w is >= 320 and <= 1920 && h is >= 200 and <= 1200, $"Implausible size {w}x{h}.");

            await console.SendInputAsync(target, [new KeyStroke("KeyA", KeyModifiers.Shift), new KeyStroke("Enter")], ct);
            await console.SendInputAsync(target, [new TypeText("Typed words, 42 times!\n")], ct);
            await console.SendInputAsync(target, [new PointerClick(w / 2, h / 2), new PointerScroll(10, 10, 0, 2), new PointerMove(0, 0)], ct);

            Assert.Empty(Directory.GetFiles(dumps));
        }
        finally
        {
            if (!qemu.HasExited)
                qemu.Kill(entireProcessTree: true);
            await qemu.WaitForExitAsync(CancellationToken.None);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }
}
