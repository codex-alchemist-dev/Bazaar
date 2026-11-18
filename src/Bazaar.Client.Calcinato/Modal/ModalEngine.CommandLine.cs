namespace Bazaar.Client.Calcinato.Modal;

public sealed partial class ModalEngine
{
    private void EnterCommandLine(char prompt, List<ModalEffect> fx)
    {
        _returnMode = Mode is ModalMode.VisualChar or ModalMode.VisualLine ? Mode : ModalMode.Normal;
        _commandPrompt = prompt;
        _commandText = string.Empty;
        _commandCursor = 0;
        _historyIndex = _history.Count;
        _draft = string.Empty;
        _count = 0;
        _pending = PendingPrefix.None;
        _pendingCount = 0;
        ClearTypedSequence();
        SetMode(fx, ModalMode.CommandLine);
        Echo(fx);
    }

    private void ExitCommandLine(List<ModalEffect> fx)
    {
        SetMode(fx, _returnMode);
        fx.Add(new ModalEffect.StatusLine(string.Empty));
    }

    private void Echo(List<ModalEffect> fx) => fx.Add(new ModalEffect.StatusLine($"{_commandPrompt}{_commandText}"));

    private void HandleCommandLine(TerminalKey key, List<ModalEffect> fx)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Text:
                if (_commandText.Length + key.Text.Length > MaxCommandLength)
                {
                    return;
                }

                _commandText = _commandText.Insert(_commandCursor, key.Text);
                _commandCursor += key.Text.Length;
                Echo(fx);
                return;

            case TerminalKeyKind.Named:
                HandleCommandLineNamed(key.Name, fx);
                return;

            default:
                return;
        }
    }

    private void HandleCommandLineNamed(KeyName name, List<ModalEffect> fx)
    {
        switch (name)
        {
            case KeyName.Escape:
                ExitCommandLine(fx);
                return;

            case KeyName.Enter:
                Submit(fx);
                return;

            case KeyName.Backspace:
                if (_commandText.Length == 0)
                {
                    ExitCommandLine(fx);
                    return;
                }

                if (_commandCursor > 0)
                {
                    _commandText = _commandText.Remove(_commandCursor - 1, 1);
                    _commandCursor--;
                }

                Echo(fx);
                return;

            case KeyName.Delete:
                if (_commandCursor < _commandText.Length)
                {
                    _commandText = _commandText.Remove(_commandCursor, 1);
                }

                Echo(fx);
                return;

            case KeyName.Left:
                _commandCursor = Math.Max(0, _commandCursor - 1);
                return;

            case KeyName.Right:
                _commandCursor = Math.Min(_commandText.Length, _commandCursor + 1);
                return;

            case KeyName.Home:
                _commandCursor = 0;
                return;

            case KeyName.End:
                _commandCursor = _commandText.Length;
                return;

            case KeyName.Up:
                HistoryStep(-1, fx);
                return;

            case KeyName.Down:
                HistoryStep(1, fx);
                return;

            default:
                return;
        }
    }

    // History is per session and only for ex commands. Searches have no history.
    private void HistoryStep(int delta, List<ModalEffect> fx)
    {
        if (_commandPrompt != ':')
        {
            return;
        }

        if (delta < 0)
        {
            if (_historyIndex == 0)
            {
                return;
            }

            if (_historyIndex == _history.Count)
            {
                _draft = _commandText;
            }

            _historyIndex--;
        }
        else
        {
            if (_historyIndex >= _history.Count)
            {
                return;
            }

            _historyIndex++;
        }

        _commandText = _historyIndex == _history.Count ? _draft : _history[_historyIndex];
        _commandCursor = _commandText.Length;
        Echo(fx);
    }

    private void Submit(List<ModalEffect> fx)
    {
        if (_commandPrompt is '/' or '?')
        {
            SubmitSearch(fx);
            return;
        }

        string text = _commandText.Trim();
        if (text.Length == 0)
        {
            ExitCommandLine(fx);
            return;
        }

        ExParseResult parsed = ExCommandParser.Parse(text);
        if (!parsed.Succeeded)
        {
            // The line stays open so the person can correct it.
            Status(fx, parsed.Error ?? "Command not understood.");
            return;
        }

        ExCommand command = parsed.Command!;
        if (command.Kind is ExCommandKind.Interrupt or ExCommandKind.Eof or ExCommandKind.Suspend)
        {
            if (!CanWrite)
            {
                Status(fx, ReadOnlyMessage);
                return;
            }

            AddHistory(text);
            ExitCommandLine(fx);
            byte value = command.Kind switch
            {
                ExCommandKind.Interrupt => InterruptByte,
                ExCommandKind.Eof => EndOfFileByte,
                _ => SuspendByte,
            };
            fx.Add(new ModalEffect.SendBytes(new[] { value }));
            Status(fx, Describe(command));
            return;
        }

        AddHistory(text);
        ExitCommandLine(fx);
        fx.Add(new ModalEffect.Command(command));
        Status(fx, Describe(command));
    }

    private void AddHistory(string text)
    {
        if (_history.Count > 0 && _history[^1] == text)
        {
            return;
        }

        _history.Add(text);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
        }
    }

    private void SubmitSearch(List<ModalEffect> fx)
    {
        bool backward = _commandPrompt == '?';
        string pattern = _commandText;
        if (pattern.Length == 0)
        {
            if (_lastPattern is null)
            {
                ExitCommandLine(fx);
                Status(fx, "No previous search pattern.");
                return;
            }

            pattern = _lastPattern;
        }
        else
        {
            _lastPattern = pattern;
        }

        _lastSearchBackward = backward;
        ExitCommandLine(fx);
        if (FindMatch(pattern, backward, _cursorRow, _cursorCol, out int row, out int col))
        {
            _cursorRow = row;
            _cursorCol = col;
            Status(fx, $"{(backward ? '?' : '/')}{pattern}");
        }
        else
        {
            Status(fx, $"Pattern not found: {pattern}");
        }
    }

    private void RepeatSearch(bool reverse, int count, List<ModalEffect> fx)
    {
        if (_lastPattern is null)
        {
            Status(fx, "No previous search pattern.");
            return;
        }

        bool backward = _lastSearchBackward ^ reverse;
        int row = _cursorRow;
        int col = _cursorCol;
        for (int i = 0; i < count; i++)
        {
            if (!FindMatch(_lastPattern, backward, row, col, out row, out col))
            {
                Status(fx, $"Pattern not found: {_lastPattern}");
                return;
            }
        }

        _cursorRow = row;
        _cursorCol = col;
    }

    // Searches the rows from the given position, wrapping around the whole buffer once.
    private bool FindMatch(string pattern, bool backward, int row, int col, out int foundRow, out int foundCol)
    {
        int total = TotalRows;
        for (int i = 0; i <= total; i++)
        {
            int r = backward ? ((row - i) % total + total) % total : (row + i) % total;
            string text = LineText(r);
            int index;
            if (!backward)
            {
                int from = i == 0 ? col + 1 : 0;
                index = from <= text.Length ? text.IndexOf(pattern, from, StringComparison.Ordinal) : -1;
            }
            else
            {
                index = LastMatchBefore(text, pattern, i == 0 ? col : int.MaxValue);
            }

            if (index >= 0)
            {
                foundRow = r;
                foundCol = index;
                return true;
            }
        }

        foundRow = row;
        foundCol = col;
        return false;
    }

    private static int LastMatchBefore(string text, string pattern, int limit)
    {
        int last = -1;
        int index = text.IndexOf(pattern, 0, StringComparison.Ordinal);
        while (index >= 0 && index < limit)
        {
            last = index;
            index = index + 1 <= text.Length ? text.IndexOf(pattern, index + 1, StringComparison.Ordinal) : -1;
        }

        return last;
    }

    private static string Describe(ExCommand command) => command.Kind switch
    {
        ExCommandKind.Quit => "Closing terminal.",
        ExCommandKind.QuitForce => "Closing terminal and ending its process.",
        ExCommandKind.New => command.Argument is null ? "Opening a new terminal." : $"Opening a new terminal with {command.Argument}.",
        ExCommandKind.VSplit => "Splitting side by side.",
        ExCommandKind.Split => "Splitting above and below.",
        ExCommandKind.Attach => $"Attaching to terminal {command.Argument}.",
        ExCommandKind.Detach => "Detaching.",
        ExCommandKind.Take => "Taking control.",
        ExCommandKind.Release => "Releasing control.",
        ExCommandKind.Set => command.Value is null ? $"Setting {command.Argument}." : $"Setting {command.Argument} to {command.Value}.",
        ExCommandKind.CrashList => "Listing crash reports.",
        ExCommandKind.CrashPreview => $"Previewing crash report {command.Number}.",
        ExCommandKind.CrashSend => $"Sending crash report {command.Number}.",
        ExCommandKind.CrashDrop => $"Dropping crash report {command.Number}.",
        ExCommandKind.Clear => "Clearing the screen.",
        ExCommandKind.Theme => "Theme.",
        ExCommandKind.Help => "Help.",
        ExCommandKind.Interrupt => "Interrupt sent.",
        ExCommandKind.Eof => "End of file sent.",
        ExCommandKind.Suspend => "Suspend sent.",
        _ => "Command.",
    };
}
