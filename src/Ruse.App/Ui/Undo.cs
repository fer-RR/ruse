using System;
using System.Collections.Generic;

namespace Ruse.App.Ui;

/// <summary>One reversible edit of the routing graph.</summary>
public interface IUndoCommand
{
    /// <summary>Short description shown in the tooltip, e.g. "Connect".</summary>
    string Label { get; }

    void Undo();

    void Redo();
}

/// <summary>A command built from two closures.</summary>
public sealed class DelegateUndoCommand : IUndoCommand
{
    private readonly Action _undo;
    private readonly Action _redo;

    public string Label { get; }

    public DelegateUndoCommand(string label, Action undo, Action redo)
    {
        Label = label;
        _undo = undo;
        _redo = redo;
    }

    public void Undo() => _undo();

    public void Redo() => _redo();
}

/// <summary>
/// A continuous value change (volume, mute, effect parameter). Consecutive
/// changes to the same target are merged, so dragging a slider leaves one
/// undo step instead of hundreds.
/// </summary>
public sealed class ValueUndoCommand : IUndoCommand
{
    private readonly Action<float> _apply;

    public string Label { get; }

    /// <summary>Identifies what is changing, so repeats can be merged.</summary>
    public string CoalesceKey { get; }

    public float OldValue { get; set; }

    public float NewValue { get; set; }

    public ValueUndoCommand(string label, string coalesceKey, Action<float> apply, float oldValue, float newValue)
    {
        Label = label;
        CoalesceKey = coalesceKey;
        _apply = apply;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public void Undo() => _apply(OldValue);

    public void Redo() => _apply(NewValue);
}

/// <summary>The undo/redo stack.</summary>
public sealed class UndoHistory
{
    private const int MaxDepth = 200;

    private readonly List<IUndoCommand> _undo = new();
    private readonly List<IUndoCommand> _redo = new();

    /// <summary>Raised whenever the stack changes, so the UI can refresh.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Label of the command that would be undone, or null.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>Label of the command that would be redone, or null.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public void Push(IUndoCommand command)
    {
        // Merge a repeated change of the same value into the previous step.
        if (command is ValueUndoCommand value && _undo.Count > 0 &&
            _undo[^1] is ValueUndoCommand previous && previous.CoalesceKey == value.CoalesceKey)
        {
            previous.NewValue = value.NewValue;
            _redo.Clear();
            Changed?.Invoke();
            return;
        }

        _undo.Add(command);
        if (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        IUndoCommand command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add(command);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        IUndoCommand command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Redo();
        _undo.Add(command);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
