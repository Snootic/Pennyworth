namespace Pennyworth.Core.Wording;

/// <summary>
/// The operating system's own words ("Explorer" or a file manager, "Recycle Bin" or "Trash").
/// Smaller than the platform seam on purpose: the assistant and the language switch need only
/// wording, and must not depend on the services that seam carries.
/// </summary>
public interface IPlatformText
{
    string Text(PlatformText text, bool portuguese);
}
