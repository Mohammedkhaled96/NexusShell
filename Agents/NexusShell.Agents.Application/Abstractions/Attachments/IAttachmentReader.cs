namespace NexusShell.Agents.Application.Abstractions.Attachments;

/// <summary>Files and folders the user attached, turned into text the model can read.</summary>
/// <param name="Text">Markdown context block (file contents, folder trees).</param>
/// <param name="IncludedFiles">Number of files whose content was included.</param>
/// <param name="Notes">What was skipped and why (too large, binary, credentials ...), for the user.</param>
public sealed record AttachmentBundle(string Text, int IncludedFiles, IReadOnlyList<string> Notes);

/// <summary>Port: reads attached files and folders (size-limited; credential files are never read).</summary>
public interface IAttachmentReader
{
    Task<AttachmentBundle> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
