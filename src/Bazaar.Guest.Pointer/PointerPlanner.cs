namespace Bazaar.Guest.Pointer;

/// <summary>Bytes to send, then a pause before the next write.</summary>
internal readonly record struct PlannedWrite(byte[] Bytes, int DelayAfterMs);

/// <summary>
/// Turns a <see cref="PointerCommand"/> into the request bytes of the virtual pointer, in order, with the pauses between them.
/// Pure: the caller gives the object id of the pointer and the time base, and nothing here touches a socket or a clock.
/// </summary>
internal static class PointerPlanner
{
    /// <summary>The pause between a press and its release, and between clicks.</summary>
    public const int HoldMs = 20;

    /// <summary>The pause after the move and before anything that acts at the new position, so the client has seen the pointer arrive.</summary>
    public const int SettleMs = 20;

    // wl_pointer axis values and the wheel source.
    private const uint _axisVertical = 0;
    private const uint _axisHorizontal = 1;
    private const uint _sourceWheel = 0;

    // One wheel click in wl_fixed units of the axis value (the compositor convention).
    private const int _wheelClickValue = 15;

    /// <summary>The writes for <paramref name="command"/>; timestamps start at <paramref name="timeBase"/> and follow the planned pauses.</summary>
    public static IReadOnlyList<PlannedWrite> Plan(PointerCommand command, uint pointerId, uint timeBase)
    {
        var writes = new List<PlannedWrite>();
        var encoder = new WaylandEncoder();
        uint now = timeBase;

        void Flush(int delayMs)
        {
            writes.Add(new PlannedWrite(encoder.Take(), delayMs));
            now += (uint)delayMs;
        }

        encoder.MotionAbsolute(pointerId, now, (uint)command.X, (uint)command.Y, (uint)command.Width, (uint)command.Height);
        encoder.Frame(pointerId);
        switch (command.Verb)
        {
            case PointerVerb.Move:
                Flush(0);
                break;
            case PointerVerb.Down:
                Flush(SettleMs);
                encoder.Button(pointerId, now, command.ButtonCode, pressed: true);
                encoder.Frame(pointerId);
                Flush(0);
                break;
            case PointerVerb.Up:
                Flush(SettleMs);
                encoder.Button(pointerId, now, command.ButtonCode, pressed: false);
                encoder.Frame(pointerId);
                Flush(0);
                break;
            case PointerVerb.Click:
                Flush(SettleMs);
                for (int i = 0; i < command.Count; i++)
                {
                    encoder.Button(pointerId, now, command.ButtonCode, pressed: true);
                    encoder.Frame(pointerId);
                    Flush(HoldMs);
                    encoder.Button(pointerId, now, command.ButtonCode, pressed: false);
                    encoder.Frame(pointerId);
                    Flush(i + 1 < command.Count ? HoldMs : 0);
                }
                break;
            default:
                Flush(SettleMs);
                Wheel(encoder, pointerId, now, _axisVertical, command.DeltaY);
                Wheel(encoder, pointerId, now, _axisHorizontal, command.DeltaX);
                Flush(0);
                break;
        }
        return writes;
    }

    // Each step is one wheel click: source, the continuous value, the discrete step, and a frame to commit it.
    private static void Wheel(WaylandEncoder encoder, uint pointerId, uint time, uint axis, int steps)
    {
        int sign = Math.Sign(steps);
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            encoder.AxisSource(pointerId, _sourceWheel);
            encoder.Axis(pointerId, time, axis, sign * _wheelClickValue);
            encoder.AxisDiscrete(pointerId, time, axis, sign * _wheelClickValue, sign);
            encoder.Frame(pointerId);
        }
    }
}
