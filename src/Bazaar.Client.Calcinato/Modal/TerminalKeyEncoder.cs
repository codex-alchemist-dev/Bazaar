using System.Text;

namespace Bazaar.Client.Calcinato.Modal;

/// <summary>
/// Encodes keys into the bytes a shell expects (xterm conventions). Cursor keys differ between normal and application
/// mode (DECCKM, DECSET 1), so the caller passes the mode that is active on the source.
/// </summary>
public static class TerminalKeyEncoder
{
    private const string Esc = "\u001b";
    private const string Csi = "\u001b[";
    private const string Ss3 = "\u001bO";

    /// <summary>Encodes one key. Text is UTF-8, raw control keys are their byte, named keys follow xterm.</summary>
    /// <param name="key">The key to encode.</param>
    /// <param name="applicationCursorKeys">True when the shell has enabled application cursor keys.</param>
    public static byte[] Encode(TerminalKey key, bool applicationCursorKeys)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Text:
                return Encoding.UTF8.GetBytes(key.Text);
            case TerminalKeyKind.RawControl:
                return [key.ControlByte];
            case TerminalKeyKind.Named:
                return EncodeNamed(key.Name, key.Shift, applicationCursorKeys);
            default:
                throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    private static byte[] EncodeNamed(KeyName name, bool shift, bool application)
    {
        switch (name)
        {
            case KeyName.Enter:
                return [0x0D];
            case KeyName.Tab:
                return shift ? Ascii(Csi + "Z") : new byte[] { 0x09 };
            case KeyName.Backspace:
                return [0x7F];
            case KeyName.Escape:
                return Ascii(Esc);
            case KeyName.Up:
                return Cursor('A', shift, application);
            case KeyName.Down:
                return Cursor('B', shift, application);
            case KeyName.Right:
                return Cursor('C', shift, application);
            case KeyName.Left:
                return Cursor('D', shift, application);
            case KeyName.Home:
                return Cursor('H', shift, application);
            case KeyName.End:
                return Cursor('F', shift, application);
            case KeyName.Insert:
                return Tilde(2, shift);
            case KeyName.Delete:
                return Tilde(3, shift);
            case KeyName.PageUp:
                return Tilde(5, shift);
            case KeyName.PageDown:
                return Tilde(6, shift);
            case KeyName.F1:
                return shift ? Ascii(Csi + "1;2P") : Ascii(Ss3 + "P");
            case KeyName.F2:
                return shift ? Ascii(Csi + "1;2Q") : Ascii(Ss3 + "Q");
            case KeyName.F3:
                return shift ? Ascii(Csi + "1;2R") : Ascii(Ss3 + "R");
            case KeyName.F4:
                return shift ? Ascii(Csi + "1;2S") : Ascii(Ss3 + "S");
            case KeyName.F5:
                return Tilde(15, shift);
            case KeyName.F6:
                return Tilde(17, shift);
            case KeyName.F7:
                return Tilde(18, shift);
            case KeyName.F8:
                return Tilde(19, shift);
            case KeyName.F9:
                return Tilde(20, shift);
            case KeyName.F10:
                return Tilde(21, shift);
            case KeyName.F11:
                return Tilde(23, shift);
            case KeyName.F12:
                return Tilde(24, shift);
            default:
                return [];
        }
    }

    // Unmodified cursor keys follow DECCKM: CSI in normal mode, SS3 in application mode. Shifted ones always use CSI 1;2.
    private static byte[] Cursor(char final, bool shift, bool application)
    {
        if (shift)
        {
            return Ascii(Csi + "1;2" + final);
        }

        return Ascii((application ? Ss3 : Csi) + final);
    }

    private static byte[] Tilde(int number, bool shift) =>
        Ascii(shift ? $"{Csi}{number};2~" : $"{Csi}{number}~");

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
