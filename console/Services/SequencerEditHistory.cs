using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Owns editor transactions and bounded Undo/Redo state. It deliberately knows only about
/// persistent document snapshots; UI selection, geometry and playback state stay outside.
/// </summary>
internal sealed class SequencerEditHistory
{
    internal const int DefaultCapacity = 50;

    private readonly int _capacity;
    private readonly List<SequenceSnapshot> _undo = new();
    private readonly List<SequenceSnapshot> _redo = new();
    private SequenceSnapshot? _activeBefore;
    private bool _activeWasDirty;

    internal SequencerEditHistory(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "History capacity must be positive.");
        _capacity = capacity;
    }

    internal bool HasActiveEdit => _activeBefore != null;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal int UndoCount => _undo.Count;
    internal int RedoCount => _redo.Count;

    internal bool Begin(SequenceSnapshot current, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (HasActiveEdit) return false;
        _activeBefore = current;
        _activeWasDirty = dirty;
        return true;
    }

    internal bool Commit(SequenceSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (_activeBefore == null) return false;

        var before = _activeBefore;
        _activeBefore = null;
        if (before.DocumentEquals(after)) return false;

        PushBounded(_undo, before);
        _redo.Clear();
        return true;
    }

    internal SequenceEditCancellation? Cancel(SequenceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_activeBefore == null) return null;

        var result = new SequenceEditCancellation(
            _activeBefore,
            _activeWasDirty,
            !_activeBefore.DocumentEquals(current));
        _activeBefore = null;
        return result;
    }

    internal SequenceSnapshot? Undo(SequenceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanUndo || HasActiveEdit) return null;

        PushBounded(_redo, current);
        return PopNewest(_undo);
    }

    internal SequenceSnapshot? Redo(SequenceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanRedo || HasActiveEdit) return null;

        PushBounded(_undo, current);
        return PopNewest(_redo);
    }

    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _activeBefore = null;
    }

    private void PushBounded(List<SequenceSnapshot> stack, SequenceSnapshot snapshot)
    {
        stack.Add(snapshot);
        if (stack.Count > _capacity) stack.RemoveAt(0);
    }

    private static SequenceSnapshot PopNewest(List<SequenceSnapshot> stack)
    {
        var index = stack.Count - 1;
        var snapshot = stack[index];
        stack.RemoveAt(index);
        return snapshot;
    }
}

internal readonly record struct SequenceEditCancellation(
    SequenceSnapshot Snapshot,
    bool WasDirty,
    bool DocumentChanged);
