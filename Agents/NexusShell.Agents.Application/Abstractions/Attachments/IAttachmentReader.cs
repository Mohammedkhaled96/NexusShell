namespace NexusShell.Agents.Application.Abstractions.Attachments;

/// <summary>Files and folders the user attached, turned into text the model can read.</summary>
/// <param name="Text">Markdown context block (file contents, folder trees).</param>
/// <param name="IncludedFiles">Number of files whose content was included.</param>
/// <param name="Notes">What was skipped and why (too large, binary, credentials ...), for the user.</param>
public sealed record AttachmentBundle(string Text, int IncludedFiles, IReadOnlyList<string> Notes);

public static class AttachmentLimits
{
    /// <summary>Attachment text when the model's context window is unknown (characters).</summary>
    public const int DefaultTotalChars = 200_000;
}

/// <summary>Port: reads attached files and folders (size-limited; credential files are never read).</summary>
public interface IAttachmentReader
{
    Task<AttachmentBundle> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        ReadAsync(paths, AttachmentLimits.DefaultTotalChars, cancellationToken);

    /// <param name="maxTotalChars">Characters of file content allowed in the message (sized from the model's context window).</param>
    Task<AttachmentBundle> ReadAsync(IReadOnlyList<string> paths, int maxTotalChars, CancellationToken cancellationToken = default);
}
