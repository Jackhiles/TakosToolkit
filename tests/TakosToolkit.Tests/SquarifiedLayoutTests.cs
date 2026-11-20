using TakosToolkit.Core.Treemap;

namespace TakosToolkit.Tests;

public class SquarifiedLayoutTests
{
    private static readonly LayoutRect Bounds = new(0, 0, 600, 400);

    [Fact]
    public void Areas_are_proportional_to_sizes_and_fill_the_bounds()
    {
        double[] sizes = [6, 6, 4, 3, 2, 2, 1];
        var rects = SquarifiedLayout.Layout(sizes, Bounds);

        double total = sizes.Sum();
        for (int i = 0; i < sizes.Length; i++)
            Assert.Equal(Bounds.Area * sizes[i] / total, rects[i].Area, 6);

        Assert.Equal(Bounds.Area, rects.Sum(r => r.Area), 6);
    }

    [Fact]
    public void Rectangles_stay_inside_the_bounds_and_do_not_overlap()
    {
        double[] sizes = Enumerable.Range(1, 40).Select(i => 1000.0 / i).ToArray();
        var rects = SquarifiedLayout.Layout(sizes, Bounds);
        const double eps = 1e-6;

        foreach (var r in rects)
        {
            Assert.True(r.X >= -eps && r.Y >= -eps);
            Assert.True(r.X + r.Width <= Bounds.Width + eps && r.Y + r.Height <= Bounds.Height + eps);
        }

        for (int i = 0; i < rects.Length; i++)
        for (int j = i + 1; j < rects.Length; j++)
        {
            var a = rects[i];
            var b = rects[j];
            double overlapW = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            double overlapH = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            Assert.False(overlapW > eps && overlapH > eps, $"items {i} and {j} overlap");
        }
    }

    [Fact]
    public void Zero_sizes_get_empty_rectangles()
    {
        var rects = SquarifiedLayout.Layout([10, 5, 0, 0], Bounds);
        Assert.True(rects[0].Area > 0 && rects[1].Area > 0);
        Assert.Equal(0, rects[2].Area);
        Assert.Equal(0, rects[3].Area);
    }

    [Fact]
    public void Empty_input_or_bounds_is_handled()
    {
        Assert.Empty(SquarifiedLayout.Layout([], Bounds));
        Assert.All(SquarifiedLayout.Layout([1, 2], new LayoutRect(0, 0, 0, 100)), r => Assert.Equal(0, r.Area));
    }
}
