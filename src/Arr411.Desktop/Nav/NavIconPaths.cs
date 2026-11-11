using System.Drawing.Drawing2D;
using Arr411.Core.Drawing;

namespace Arr411.Desktop.Nav;

/// <summary>Turns the icons of <see cref="NavIcons"/> into paths that GDI+ can stroke.</summary>
internal static class NavIconPaths
{
    /// <summary>The three lines of the menu entry (Heroicons "bars-3", same family as the tab icons).</summary>
    public static SvgPath Menu { get; } = SvgPath.Parse("M 3.75 6.75 h 16.5 M 3.75 12 h 16.5 M 3.75 17.25 h 16.5");

    /// <summary>The path of an icon, scaled so that its 24 × 24 grid fills <paramref name="bounds"/> (a square).</summary>
    public static GraphicsPath Build(SvgPath icon, RectangleF bounds)
    {
        var scale = bounds.Width / NavIcons.Grid;
        PointF Map(PointF point) => new(bounds.X + point.X * scale, bounds.Y + point.Y * scale);

        var path = new GraphicsPath(FillMode.Winding);
        var current = PointF.Empty;
        var start = PointF.Empty;
        foreach (var segment in icon.Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.MoveTo:
                    path.StartFigure();
                    current = start = Map(segment.End);
                    break;
                case PathVerb.LineTo:
                {
                    var end = Map(segment.End);
                    path.AddLine(current, end);
                    current = end;
                    break;
                }

                case PathVerb.CubicTo:
                {
                    var end = Map(segment.End);
                    path.AddBezier(current, Map(segment.Control1), Map(segment.Control2), end);
                    current = end;
                    break;
                }

                case PathVerb.Close:
                    path.CloseFigure();
                    current = start;
                    break;
            }
        }

        return path;
    }
}
