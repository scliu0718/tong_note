using Tonote.Core;

namespace Tonote.App;

public sealed class AppSession
{
    public AppSession(string? root = null)
    {
        Root = root ?? AppPaths.DefaultRoot();
        Notes = new NoteStore(Root);
        Todos = new TodoStore(Root);
        Notebooks = new NotebookStore(Root);
    }

    public string Root { get; }
    public NoteStore Notes { get; }
    public TodoStore Todos { get; }
    public NotebookStore Notebooks { get; }
}
