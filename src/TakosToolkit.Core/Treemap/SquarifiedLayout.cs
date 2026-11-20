namespace TakosToolkit.Core.Treemap;

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk). Produces rectangles with aspect
/// ratios close to 1, which are much easier to read and click than slice-and-dice strips.
/// </summary>
public static class SquarifiedLayout
{
    /// <param name="sizes">Item sizes sorted largest first. Items with size 0 or less get an empty rectangle.</param>
    /// <param name="bounds">The area to fill.</param>
    /// <returns>One rectangle per input item, in the same order.</returns>
    public static LayoutRect[] Layout(IReadOnlyList<double> sizes, LayoutRect bounds)
    {
        var result = new LayoutRect[sizes.Count];

        int count = 0;
        double total = 0;
        while (count < sizes.Count && sizes[count] > 0)
            total += sizes[count++];

        if (count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return result;

        double scale = bounds.Area / total;
        var rect = bounds;
        int start = 0;

        while (start < count)
        {
            double side = Math.Min(rect.Width, rect.Height);
            if (side <= 0)
                break;

            double max = sizes[start] * scale;
            double rowSum = 0;
            double worst = double.MaxValue;
            int end = start;

            while (end < count)
            {
                double area = sizes[end] * scale;
                double newSum = rowSum + area;
                double newWorst = Worst(max, area, newSum, side);
                if (end > start && newWorst > worst)
                    break;
                rowSum = newSum;
                worst = newWorst;
                end++;
            }

            if (rect.Width >= rect.Height)
            {
                // Lay the row out as a column on the left.
                double width = Math.Min(rowSum / rect.Height, rect.Width);
                double y = rect.Y;
                for (int i = start; i < end; i++)
                {
                    double h = sizes[i] * scale / width;
                    result[i] = new LayoutRect(rect.X, y, width, h);
                    y += h;
                }
                rect = new LayoutRect(rect.X + width, rect.Y, Math.Max(0, rect.Width - width), rect.Height);
            }
            else
            {
                // Lay the row out across the top.
                double height = Math.Min(rowSum / rect.Width, rect.Height);
                double x = rect.X;
                for (int i = start; i < end; i++)
                {
                    double w = sizes[i] * scale / height;
                    result[i] = new LayoutRect(x, rect.Y, w, height);
                    x += w;
                }
                rect = new LayoutRect(rect.X, rect.Y + height, rect.Width, Math.Max(0, rect.Height - height));
            }

            start = end;
        }

        return result;
    }

    /// <summary>The worst (largest) aspect ratio in a row with the given max/min item areas.</summary>
    private static double Worst(double maxArea, double minArea, double rowSum, double side)
    {
        double s2 = rowSum * rowSum;
        double w2 = side * side;
        return Math.Max(w2 * maxArea / s2, s2 / (w2 * minArea));
    }
}
