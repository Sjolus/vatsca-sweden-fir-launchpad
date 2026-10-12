namespace VatscaUpdateChecker.Models;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    public static WindowBounds Fit(WindowBounds saved, WindowBounds workArea, double minWidth, double minHeight)
    {
        if (!workArea.IsValid) throw new ArgumentException("A valid work area is required.");
        if (!saved.IsValid) saved = new(workArea.Left, workArea.Top, minWidth, minHeight);
        double width = Math.Min(workArea.Width, Math.Max(minWidth, saved.Width));
        double height = Math.Min(workArea.Height, Math.Max(minHeight, saved.Height));
        return new(Math.Clamp(saved.Left, workArea.Left, workArea.Left + workArea.Width - width),
            Math.Clamp(saved.Top, workArea.Top, workArea.Top + workArea.Height - height), width, height);
    }
}
