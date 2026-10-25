using Vellum.Domain;

namespace Vellum.Infrastructure.Files;

/// <summary>Outcome of an <see cref="IAttachmentPolicy"/> check for one file request.</summary>
public sealed record AttachmentDecision(bool Allowed, string? RedirectUrl = null, string? Reason = null)
{
    public static readonly AttachmentDecision Allow = new(true);
    public static AttachmentDecision Deny(string reason) => new(false, null, reason);
    public static AttachmentDecision Redirect(string url) => new(false, url);
}

/// <summary>
/// Extra rules for serving attachments through <c>/files/{id}</c>, applied after the record read-access check. Modules
/// register implementations with <c>[ScopedService(typeof(IAttachmentPolicy))]</c>; for example the PDF engine hides
/// editable originals in PDF-only phases (DOC-023) and sends renditions to its viewer, which applies DRAFT/SUPERSEDED
/// marking and download permissions (SAF-043, DOC-081).
/// </summary>
public interface IAttachmentPolicy
{
    Task<AttachmentDecision> DecideAsync(Attachment attachment, bool inline);
}
