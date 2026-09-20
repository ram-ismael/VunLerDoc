namespace VunLerDoc.Services;

/// <summary>
/// Clickable region discovered in a PDF page. Bounds are normalized to the page bitmap in its
/// original reader orientation (0..1, top-left origin), so they remain independent of DPI/zoom.
/// User rotation is handled by the view-model hit test before these bounds are consulted.
/// </summary>
public sealed record PdfLinkInfo(
    double Left,
    double Top,
    double Right,
    double Bottom,
    int? TargetPageIndex,
    string? Uri)
{
    public bool Contains(double normalizedX, double normalizedY)
        => normalizedX >= Left && normalizedX <= Right &&
           normalizedY >= Top && normalizedY <= Bottom;

    public bool IsInternal => TargetPageIndex is not null;
    public bool IsExternal => !string.IsNullOrWhiteSpace(Uri);
}
