namespace Toolbox.Core;

/// <summary>A failure whose message is written for the user (shown in toasts and job results).</summary>
public class ProcessingException : Exception
{
    public ProcessingException(string message) : base(message) { }

    public ProcessingException(string message, Exception inner) : base(message, inner) { }
}
