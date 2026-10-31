namespace CleanRift.UI.Controls;

/// <summary>
/// ListView with double buffering enabled. Owner-drawn rows otherwise flicker
/// and can lose text when Windows invalidates only the hovered sub-item.
/// </summary>
public sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }
}
