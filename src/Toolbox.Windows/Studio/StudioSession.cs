using Toolbox.Processing.Studio;

namespace Toolbox.Windows.Studio;

/// <summary>
/// The editing session behind the Studio window: the current document plus undo/redo history.
/// Every change produces a new immutable <see cref="EditDocument"/>.
/// </summary>
internal sealed class StudioSession
{
    private readonly Stack<EditDocument> _undo = new();
    private readonly Stack<EditDocument> _redo = new();
    private EditDocument? _transactionStart;

    public StudioSession(EditDocument initial, StudioSource source)
    {
        Document = initial;
        Initial = initial;
        Source = source;
    }

    public EditDocument Document { get; private set; }
    public EditDocument Initial { get; }
    public StudioSource Source { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every change (including undo/redo) with the new document.</summary>
    public event Action<EditDocument>? Changed;

    /// <summary>Applies a change as one undo step.</summary>
    public void Update(Func<EditDocument, EditDocument> change)
    {
        var next = change(Document);
        if (next == Document) return;
        if (_transactionStart is null) _undo.Push(Document);
        _redo.Clear();
        Document = next;
        Changed?.Invoke(Document);
    }

    /// <summary>Groups continuous changes (slider drags, handle drags) into a single undo step.</summary>
    public void BeginTransaction() => _transactionStart ??= Document;

    public void EndTransaction()
    {
        if (_transactionStart is { } start && start != Document) _undo.Push(start);
        _transactionStart = null;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Document);
        Document = _undo.Pop();
        Changed?.Invoke(Document);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(Document);
        Document = _redo.Pop();
        Changed?.Invoke(Document);
    }

    /// <summary>Switches to another PDF page (resets the history: geometry belongs to the page).</summary>
    public void ReplaceSource(StudioSource source, EditDocument document)
    {
        var old = Source;
        Source = source;
        _undo.Clear();
        _redo.Clear();
        Document = document;
        old.Dispose();
        Changed?.Invoke(Document);
    }
}
