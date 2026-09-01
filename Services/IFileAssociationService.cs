namespace VunLerDoc.Services;

/// <summary>
/// Registers VunLerDoc as an option in the OS's "Open with" / default-apps UI for .pdf files.
/// Implementations must be safe to call unconditionally on every startup (idempotent, cheap
/// once already registered, and must never throw into the caller).
/// </summary>
public interface IFileAssociationService
{
    void EnsureRegistered();
}
