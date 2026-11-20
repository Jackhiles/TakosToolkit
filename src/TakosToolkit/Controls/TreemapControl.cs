using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TakosToolkit.Core;
using TakosToolkit.Core.DiskScanning;
using TakosToolkit.Core.Treemap;

namespace TakosToolkit.Controls;

/// <summary>
/// Draws a nested squarified treemap of a <see cref="FileNode"/> tree. Folders get a title strip
/// with their name and size; files are colored by category.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double HeaderHeight = 16;
    private const double Padding = 2;
    private const double MinFolderToRecurse = 14;
    private const int MaxDepth = 32;

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FileNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapControl)d).Invalidate()));

    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _visuals;
    private readonly List<Item> _items = [];
    private Size _laidOutSize;
    private FileNode? _laidOutRoot;
    private Item? _hovered;
    private Item? _selected;

    private readonly record struct Item(FileNode Node, Rect Bounds, int Depth);

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _content, _overlay };
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
    }

    public FileNode? Root
    {
        get => (FileNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public FileNode? SelectedNode => _selected?.Node;

    /// <summary>Raised when the pointer moves onto a different item (null when it leaves).</summary>
    public event EventHandler<FileNode?>? HoveredNodeChanged;

    public event EventHandler<FileNode?>? SelectedNodeChanged;

    /// <summary>Raised on double-click. The page decides what to do (e.g. zoom into a folder).</summary>
    public event EventHandler<FileNode>? NodeActivated;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void Invalidate()
    {
        _laidOutRoot = null;
        _hovered = null;
        _selected = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Paint a background on the element itself so the whole area is hit-testable.
        dc.DrawRectangle(Palette.Background, null, new Rect(RenderSize));

        if (!ReferenceEquals(_laidOutRoot, Root) || _laidOutSize != RenderSize)
            Rebuild();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    // ---- Layout & drawing ------------------------------------------------------------------

    private void Rebuild()
    {
        _laidOutRoot = Root;
        _laidOutSize = RenderSize;
        _items.Clear();
        _hovered = null;
        _selected = null;

        using (var dc = _content.RenderOpen())
        {
            if (Root is { Size: > 0 } root && RenderSize.Width > 4 && RenderSize.Height > 4)
            {
                LayoutChildren(root, new Rect(RenderSize), 0);
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                foreach (var item in _items)
                    DrawItem(dc, item, dpi);
            }
            else if (Root is not null)
            {
                DrawCenteredText(dc, "This folder is empty.");
            }
        }

        DrawOverlay();
    }

    private void LayoutChildren(FileNode folder, Rect area, int depth)
    {
        var children = folder.Children;
        int count = 0;
        while (count < children.Count && children[count].Size > 0)
            count++;
        if (count == 0)
            return;

        var sizes = new double[count];
        for (int i = 0; i < count; i++)
            sizes[i] = children[i].Size;

        var rects = SquarifiedLayout.Layout(sizes, new LayoutRect(area.X, area.Y, area.Width, area.Height));

        for (int i = 0; i < count; i++)
        {
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1)
                continue;

            var child = children[i];
            var bounds = new Rect(r.X, r.Y, r.Width, r.Height);
            _items.Add(new Item(child, bounds, depth));

            if (child.IsDirectory && depth < MaxDepth &&
                bounds.Width > MinFolderToRecurse && bounds.Height > MinFolderToRecurse + HeaderHeight)
            {
                var inner = new Rect(
                    bounds.X + Padding,
                    bounds.Y + HeaderHeight,
                    Math.Max(0, bounds.Width - Padding * 2),
                    Math.Max(0, bounds.Height - HeaderHeight - Padding));
                LayoutChildren(child, inner, depth + 1);
            }
        }
    }

    private static void DrawItem(DrawingContext dc, Item item, double dpi)
    {
        var b = item.Bounds;
        var node = item.Node;

        if (node.IsDirectory)
        {
            dc.DrawRectangle(Palette.FolderFill(item.Depth), Palette.FolderBorder, b);
            if (b.Height > HeaderHeight + 2 && b.Width > 30)
            {
                string label = $"{node.Name}  {ByteSize.Format(node.Size)}";
                DrawLabel(dc, label, new Rect(b.X + 4, b.Y + 1, b.Width - 8, HeaderHeight - 2), Palette.FolderText, dpi, bold: true);
            }
        }
        else
        {
            var category = FileCategorizer.Get(node);
            dc.DrawRectangle(Palette.FileFill(category), b.Width > 3 && b.Height > 3 ? Palette.FileBorder : null, b);
            if (b.Width > 60 && b.Height > 28)
            {
                DrawLabel(dc, node.Name, new Rect(b.X + 4, b.Y + 3, b.Width - 8, 14), Palette.FileText, dpi, bold: false);
                DrawLabel(dc, ByteSize.Format(node.Size), new Rect(b.X + 4, b.Y + 16, b.Width - 8, 14), Palette.FileTextDim, dpi, bold: false);
            }
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect area, Brush brush, double dpi, bool bold)
    {
        if (area.Width < 8)
            return;

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? Palette.BoldFace : Palette.Face, 11, brush, dpi)
        {
            MaxTextWidth = area.Width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, area.TopLeft);
    }

    private void DrawCenteredText(DrawingContext dc, string text)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Palette.Face, 14, Palette.FolderText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((RenderSize.Width - ft.Width) / 2, (RenderSize.Height - ft.Height) / 2));
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_selected is { } sel)
            dc.DrawRectangle(Palette.SelectedFill, Palette.SelectedBorder, sel.Bounds);
        if (_hovered is { } hov && !Equals(hov, _selected))
            dc.DrawRectangle(Palette.HoverFill, Palette.HoverBorder, hov.Bounds);
    }

    // ---- Input -----------------------------------------------------------------------------

    private Item? HitTest(Point p)
    {
        // Items are stored parents-first, so the last match is the deepest one.
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Bounds.Contains(p))
                return _items[i];
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (!Equals(hit, _hovered))
        {
            _hovered = hit;
            DrawOverlay();
            HoveredNodeChanged?.Invoke(this, hit?.Node);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is not null)
        {
            _hovered = null;
            DrawOverlay();
            HoveredNodeChanged?.Invoke(this, null);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right))
            return;

        Focus();
        var hit = HitTest(e.GetPosition(this));
        if (!Equals(hit, _selected))
        {
            _selected = hit;
            DrawOverlay();
            SelectedNodeChanged?.Invoke(this, hit?.Node);
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && hit is { } item)
            NodeActivated?.Invoke(this, item.Node);
    }

    // ---- Colors ----------------------------------------------------------------------------

    public static class Palette
    {
        public static readonly Typeface Face = new("Segoe UI");
        public static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)));
        public static readonly Brush FolderText = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)));
        public static readonly Brush FileText = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
        public static readonly Brush FileTextDim = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
        public static readonly Pen FolderBorder = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)), 1));
        public static readonly Pen FileBorder = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1));
        public static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
        public static readonly Pen HoverBorder = Freeze(new Pen(Brushes.White, 1.5));
        public static readonly Brush SelectedFill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x60, 0xCD, 0xFF)));
        public static readonly Pen SelectedBorder = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)), 2));

        private static readonly Brush[] FolderFills =
        [
            Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x33))),
            Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3D))),
            Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x3F, 0x47))),
            Freeze(new SolidColorBrush(Color.FromRgb(0x43, 0x48, 0x51))),
        ];

        private static readonly Dictionary<FileCategory, Brush> FileFills = new();

        public static Color CategoryColor(FileCategory c) => c switch
        {
            FileCategory.Video => Color.FromRgb(0x8E, 0x5C, 0xD9),
            FileCategory.Audio => Color.FromRgb(0xD9, 0x4F, 0x9A),
            FileCategory.Image => Color.FromRgb(0x3F, 0xA8, 0x5B),
            FileCategory.Archive => Color.FromRgb(0xE0, 0x84, 0x2E),
            FileCategory.DiskImage => Color.FromRgb(0xD6, 0x4B, 0x4B),
            FileCategory.Program => Color.FromRgb(0x3A, 0x86, 0xD6),
            FileCategory.Document => Color.FromRgb(0xC9, 0xA8, 0x2C),
            FileCategory.Code => Color.FromRgb(0x2A, 0xA8, 0x9A),
            FileCategory.MailAndData => Color.FromRgb(0xA8, 0x6A, 0x3E),
            FileCategory.GameData => Color.FromRgb(0x5B, 0x6E, 0xC9),
            FileCategory.System => Color.FromRgb(0x7A, 0x84, 0x8F),
            _ => Color.FromRgb(0x56, 0x63, 0x73),
        };

        public static Brush FolderFill(int depth) => FolderFills[depth % FolderFills.Length];

        /// <summary>A soft gradient per category gives each file a slight "cushion" look.</summary>
        public static Brush FileFill(FileCategory c)
        {
            if (FileFills.TryGetValue(c, out var brush))
                return brush;

            var color = CategoryColor(c);
            var light = Color.FromRgb(Lerp(color.R, 255, 0.18), Lerp(color.G, 255, 0.18), Lerp(color.B, 255, 0.18));
            var dark = Color.FromRgb(Lerp(color.R, 0, 0.25), Lerp(color.G, 0, 0.25), Lerp(color.B, 0, 0.25));
            brush = Freeze(new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 1)));
            FileFills[c] = brush;
            return brush;
        }

        public static Brush CategoryBrush(FileCategory c) => Freeze(new SolidColorBrush(CategoryColor(c)));

        private static byte Lerp(byte from, byte to, double t) => (byte)(from + (to - from) * t);

        private static T Freeze<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
