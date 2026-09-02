namespace VunLerDoc.Services;

public sealed class PrintOptions
{
    public string PrinterName { get; set; } = string.Empty;
    public int Copies { get; set; } = 1;
    public bool Landscape { get; set; }
}