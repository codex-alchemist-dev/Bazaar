namespace Bazaar.Client.Calcinato.Modal;

public sealed partial class ModalEngine
{
    private void HandleNormalOrVisual(TerminalKey key, List<ModalEffect> fx)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Named:
                HandleNamed(key, fx);
                return;
            case TerminalKeyKind.RawControl:
                ClearTypedSequence();
                Status(fx, "Control keys go to the shell in Insert mode. Press i to type.");
                return;
            default:
                HandleTyped(key.Text[0], fx);
                return;
        }
    }

    private void HandleNamed(TerminalKey key, List<ModalEffect> fx)
    {
        ClearTypedSequence();
        _pending = PendingPrefix.None;
        _pendingCount = 0;
        int count = TakeCount();
        switch (key.Name)
        {
            case KeyName.Escape:
                if (Mode is ModalMode.VisualChar or ModalMode.VisualLine)
                {
                    SetMode(fx, ModalMode.Normal);
                }

                return;
            case KeyName.Left:
            case KeyName.Backspace:
                ExecuteAction(ModalAction.MoveLeft, count, fx);
                return;
            case KeyName.Right:
                ExecuteAction(ModalAction.MoveRight, count, fx);
                return;
            case KeyName.Up:
                ExecuteAction(ModalAction.MoveUp, count, fx);
                return;
            case KeyName.Down:
                ExecuteAction(ModalAction.MoveDown, count, fx);
                return;
            case KeyName.Home:
                GoToRow(count == 0 ? 0 : count - 1);
                return;
            case KeyName.End:
                GoToRow(count == 0 ? TotalRows - 1 : count - 1);
                return;
            case KeyName.PageUp:
                ExecuteAction(ModalAction.PageUp, count, fx);
                return;
            case KeyName.PageDown:
                ExecuteAction(ModalAction.PageDown, count, fx);
                return;
            case KeyName.Insert:
                RequestInsert(fx);
                return;
            default:
                return;
        }
    }

    private void HandleTyped(char c, List<ModalEffect> fx)
    {
        if (_pending != PendingPrefix.None)
        {
            ResolvePending(c, fx);
            return;
        }

        if (_typedSequence.Length > 0)
        {
            _typedSequence.Append(c);
            ResolveTypedSequence(fx);
            return;
        }

        if (c is >= '1' and <= '9' || (c == '0' && _count > 0))
        {
            _count = Math.Min(_count * 10 + (c - '0'), MaxCount);
            return;
        }

        if (_keymap.TryGetAction(c, out ModalAction action))
        {
            int count = TakeCount();
            ExecuteAction(action, count, fx);
            return;
        }

        TakeCount();
        if (Mode != ModalMode.Normal)
        {
            Status(fx, $"No command: {c}");
            return;
        }

        _typedSequence.Append(c);
        ResolveTypedSequence(fx);
    }

    private int TakeCount()
    {
        int count = _count;
        _count = 0;
        return count;
    }

    private void ClearTypedSequence() => _typedSequence.Clear();

    // A typed sequence waits while a longer mnemonic (or the built-in intr) could still complete it.
    private void ResolveTypedSequence(List<ModalEffect> fx)
    {
        string typed = _typedSequence.ToString();
        bool longerPossible = (_mnemonics?.IsPrefix(typed) ?? false)
            || (typed.Length < InterruptMnemonic.Length && InterruptMnemonic.StartsWith(typed, StringComparison.Ordinal));
        if (longerPossible)
        {
            return;
        }

        _typedSequence.Clear();
        if (typed == InterruptMnemonic)
        {
            SendToShell(fx, new[] { InterruptByte }, "Interrupt sent.");
            return;
        }

        if (_mnemonics is not null && _mnemonics.TryHandle(typed, out string? speech))
        {
            Status(fx, speech ?? $"Ran {typed}.");
            return;
        }

        Status(fx, $"No command: {typed}");
    }

    private void ResolvePending(char c, List<ModalEffect> fx)
    {
        var pending = _pending;
        int count = _pendingCount;
        _pending = PendingPrefix.None;
        _pendingCount = 0;
        switch (pending)
        {
            case PendingPrefix.GPrefix:
                if (c == 'g')
                {
                    GoToRow(count == 0 ? 0 : count - 1);
                }
                else
                {
                    Status(fx, $"No command: g{c}");
                }

                return;

            case PendingPrefix.SetMark:
                if (c is >= 'a' and <= 'z')
                {
                    _marks[c] = (_cursorRow, _cursorCol);
                    Status(fx, $"Mark {c} set.");
                }
                else
                {
                    Status(fx, "Marks are lowercase letters a to z.");
                }

                return;

            case PendingPrefix.JumpMark:
                if (_marks.TryGetValue(c, out var mark))
                {
                    _cursorRow = mark.Row;
                    _cursorCol = mark.Col;
                }
                else
                {
                    Status(fx, $"Mark {c} is not set.");
                }

                return;
        }
    }

    private void ExecuteAction(ModalAction action, int count, List<ModalEffect> fx)
    {
        int n = Math.Max(1, count);
        switch (action)
        {
            case ModalAction.MoveLeft:
                MoveColumn(-n);
                break;
            case ModalAction.MoveRight:
                MoveColumn(n);
                break;
            case ModalAction.MoveUp:
                MoveRow(-n);
                break;
            case ModalAction.MoveDown:
                MoveRow(n);
                break;
            case ModalAction.WordForward:
                MoveByWord(n, WordMotion.Forward);
                break;
            case ModalAction.WordBackward:
                MoveByWord(n, WordMotion.Backward);
                break;
            case ModalAction.WordEnd:
                MoveByWord(n, WordMotion.End);
                break;
            case ModalAction.LineStart:
                _cursorCol = 0;
                break;
            case ModalAction.LineEnd:
                _cursorCol = MaxColumn(_cursorRow);
                break;
            case ModalAction.GoBottom:
                GoToRow(count == 0 ? TotalRows - 1 : count - 1);
                break;
            case ModalAction.HalfPageDown:
                MoveRow(Math.Max(1, _screen.Rows / 2) * n);
                break;
            case ModalAction.HalfPageUp:
                MoveRow(-Math.Max(1, _screen.Rows / 2) * n);
                break;
            case ModalAction.PageDown:
                MoveRow(PageSize * n);
                break;
            case ModalAction.PageUp:
                MoveRow(-PageSize * n);
                break;
            case ModalAction.SearchForward:
                EnterCommandLine('/', fx);
                break;
            case ModalAction.SearchBackward:
                EnterCommandLine('?', fx);
                break;
            case ModalAction.SearchNext:
                RepeatSearch(reverse: false, n, fx);
                break;
            case ModalAction.SearchPrevious:
                RepeatSearch(reverse: true, n, fx);
                break;
            case ModalAction.SetMark:
                _pending = PendingPrefix.SetMark;
                _pendingCount = count;
                break;
            case ModalAction.JumpMark:
                _pending = PendingPrefix.JumpMark;
                _pendingCount = count;
                break;
            case ModalAction.GPrefix:
                _pending = PendingPrefix.GPrefix;
                _pendingCount = count;
                break;
            case ModalAction.InsertBefore:
            case ModalAction.InsertAfter:
            case ModalAction.InsertLineStart:
            case ModalAction.InsertLineEnd:
                RequestInsert(fx);
                break;
            case ModalAction.VisualChar:
                ToggleVisual(ModalMode.VisualChar, fx);
                break;
            case ModalAction.VisualLine:
                ToggleVisual(ModalMode.VisualLine, fx);
                break;
            case ModalAction.Yank:
                if (Mode is ModalMode.VisualChar or ModalMode.VisualLine)
                {
                    YankSelection(fx);
                }
                else
                {
                    Status(fx, "Yank works in Visual mode. Press v or V first.");
                }

                break;
            case ModalAction.CommandLine:
                EnterCommandLine(':', fx);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }
    }

    private void ToggleVisual(ModalMode kind, List<ModalEffect> fx)
    {
        if (Mode == kind)
        {
            SetMode(fx, ModalMode.Normal);
            return;
        }

        if (Mode == ModalMode.Normal)
        {
            _anchorRow = _cursorRow;
            _anchorCol = _cursorCol;
        }

        SetMode(fx, kind);
    }

    private void MoveRow(int delta)
    {
        _cursorRow = Math.Clamp(_cursorRow + delta, 0, TotalRows - 1);
        _cursorCol = Math.Min(_cursorCol, MaxColumn(_cursorRow));
    }

    private void MoveColumn(int delta)
    {
        if (delta < 0)
        {
            _cursorCol = Math.Max(0, _cursorCol + delta);
            return;
        }

        int limit = Math.Max(MaxColumn(_cursorRow), _cursorCol);
        _cursorCol = Math.Min(limit, _cursorCol + delta);
    }

    private void GoToRow(int row)
    {
        _cursorRow = Math.Clamp(row, 0, TotalRows - 1);
        _cursorCol = 0;
    }

    private void MoveByWord(int count, WordMotion.Step step)
    {
        for (int i = 0; i < count; i++)
        {
            (_cursorRow, _cursorCol) = step(LineText, TotalRows, _cursorRow, _cursorCol);
        }
    }

    private int MaxColumn(int row) => Math.Min(Math.Max(0, LineText(row).Length - 1), Math.Max(0, _screen.Cols - 1));

    private void YankSelection(List<ModalEffect> fx)
    {
        bool anchorFirst = _anchorRow < _cursorRow || (_anchorRow == _cursorRow && _anchorCol <= _cursorCol);
        int startRow = anchorFirst ? _anchorRow : _cursorRow;
        int startCol = anchorFirst ? _anchorCol : _cursorCol;
        int endRow = anchorFirst ? _cursorRow : _anchorRow;
        int endCol = anchorFirst ? _cursorCol : _anchorCol;
        int lines = endRow - startRow + 1;

        string text;
        if (Mode == ModalMode.VisualLine)
        {
            var rows = new List<string>(lines);
            for (int row = startRow; row <= endRow; row++)
            {
                rows.Add(LineText(row));
            }

            text = string.Join('\n', rows);
        }
        else if (startRow == endRow)
        {
            text = Slice(LineText(startRow), startCol, endCol + 1);
        }
        else
        {
            var parts = new List<string> { Slice(LineText(startRow), startCol, int.MaxValue) };
            for (int row = startRow + 1; row < endRow; row++)
            {
                parts.Add(LineText(row));
            }

            parts.Add(Slice(LineText(endRow), 0, endCol + 1));
            text = string.Join('\n', parts);
        }

        fx.Add(new ModalEffect.Yank(text));
        SetMode(fx, ModalMode.Normal);
        Status(fx, $"Yanked {text.Length} characters from {lines} line(s).");
    }

    private static string Slice(string text, int start, int endExclusive)
    {
        int s = Math.Clamp(start, 0, text.Length);
        int e = Math.Clamp(endExclusive, s, text.Length);
        return text[s..e];
    }
}
