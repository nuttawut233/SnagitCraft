using System;
using System.Collections.Generic;

namespace SnagitAnything.Editor;

public sealed class Edit
{
    public Edit(Action undo, Action redo) { Undo = undo; Redo = redo; }
    public Action Undo { get; }
    public Action Redo { get; }
}

public sealed class UndoStack
{
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private List<Edit>? _group;

    public event Action? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty { get; set; }

    /// <summary>Records an edit that has already been applied.</summary>
    public void Push(Edit edit)
    {
        if (_group is not null)
        {
            _group.Add(edit);
            return;
        }
        _undo.Push(edit);
        _redo.Clear();
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <summary>Edits pushed until <see cref="EndGroup"/> become one undo step (e.g. "grow canvas + paste").</summary>
    public void BeginGroup() => _group ??= new List<Edit>();

    public void EndGroup()
    {
        var edits = _group;
        _group = null;
        if (edits is null || edits.Count == 0) return;
        if (edits.Count == 1)
        {
            Push(edits[0]);
            return;
        }
        Push(new Edit(
            () => { for (int i = edits.Count - 1; i >= 0; i--) edits[i].Undo(); },
            () => { foreach (var e in edits) e.Redo(); }));
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var e = _undo.Pop();
        e.Undo();
        _redo.Push(e);
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var e = _redo.Pop();
        e.Redo();
        _undo.Push(e);
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _group = null;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
