using System.Drawing;
using Arr411.Core.Drawing;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class SvgPathTests
{
    private static IReadOnlyList<PathSegment> Parse(string data) => SvgPath.Parse(data).Segments;

    private static void Near(double expected, double actual, double tolerance = 1e-3) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static void At(PointF actual, double x, double y)
    {
        Near(x, actual.X);
        Near(y, actual.Y);
    }

    private static void Step(PathSegment segment, PathVerb verb, double x, double y)
    {
        Assert.Equal(verb, segment.Verb);
        At(segment.End, x, y);
    }

    // The points a path goes through: its vertices, and three more on every curve.
    private static List<PointF> Sample(IReadOnlyList<PathSegment> segments)
    {
        var points = new List<PointF>();
        var current = PointF.Empty;
        foreach (var segment in segments)
        {
            if (segment.Verb == PathVerb.CubicTo)
            {
                for (var i = 1; i <= 4; i++)
                {
                    var t = i / 4.0;
                    var u = 1 - t;
                    points.Add(new PointF(
                        (float)(u * u * u * current.X + 3 * u * u * t * segment.Control1.X + 3 * u * t * t * segment.Control2.X + t * t * t * segment.End.X),
                        (float)(u * u * u * current.Y + 3 * u * u * t * segment.Control1.Y + 3 * u * t * t * segment.Control2.Y + t * t * t * segment.End.Y)));
                }

                current = segment.End;
            }
            else if (segment.Verb is PathVerb.MoveTo or PathVerb.LineTo)
            {
                current = segment.End;
                points.Add(current);
            }
        }

        return points;
    }

    private static void OnCircle(IEnumerable<PointF> points, double centreX, double centreY, double radius)
    {
        foreach (var point in points)
        {
            var distance = Math.Sqrt(Math.Pow(point.X - centreX, 2) + Math.Pow(point.Y - centreY, 2));
            Assert.InRange(distance, radius - 0.005, radius + 0.005);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,\n")]
    public void Nothing_to_draw_is_an_empty_path(string data)
    {
        Assert.Empty(Parse(data));
    }

    [Fact]
    public void Absolute_lines_and_close()
    {
        var segments = Parse("M 1 2 L 3 4 Z");

        Assert.Equal(3, segments.Count);
        Step(segments[0], PathVerb.MoveTo, 1, 2);
        Step(segments[1], PathVerb.LineTo, 3, 4);
        Assert.Equal(PathVerb.Close, segments[2].Verb);
    }

    [Fact]
    public void Relative_commands_start_from_the_current_point()
    {
        var segments = Parse("m 1 2 l 3 4 l -1 -1 h 2 v 5");

        Step(segments[0], PathVerb.MoveTo, 1, 2);
        Step(segments[1], PathVerb.LineTo, 4, 6);
        Step(segments[2], PathVerb.LineTo, 3, 5);
        Step(segments[3], PathVerb.LineTo, 5, 5);
        Step(segments[4], PathVerb.LineTo, 5, 10);
    }

    [Fact]
    public void Horizontal_and_vertical_lines_keep_the_other_coordinate()
    {
        var segments = Parse("M 1 1 H 5 V 7 H 2");

        Step(segments[1], PathVerb.LineTo, 5, 1);
        Step(segments[2], PathVerb.LineTo, 5, 7);
        Step(segments[3], PathVerb.LineTo, 2, 7);
    }

    [Fact]
    public void More_numbers_repeat_the_command()
    {
        var absolute = Parse("M 0 0 1 1 2 2");
        Assert.Equal(3, absolute.Count);
        Step(absolute[1], PathVerb.LineTo, 1, 1);
        Step(absolute[2], PathVerb.LineTo, 2, 2);

        var relative = Parse("m 1 1 1 1 1 1");
        Step(relative[0], PathVerb.MoveTo, 1, 1);
        Step(relative[1], PathVerb.LineTo, 2, 2);
        Step(relative[2], PathVerb.LineTo, 3, 3);

        var horizontal = Parse("M 0 0 H 1 2 3");
        Assert.Equal(4, horizontal.Count);
        Step(horizontal[3], PathVerb.LineTo, 3, 0);
    }

    [Fact]
    public void Closing_goes_back_to_the_start_of_the_figure()
    {
        var segments = Parse("M 1 1 L 5 1 L 5 5 z l 1 0 M 9 9 h -1 Z h 2");

        Assert.Equal(9, segments.Count);
        Assert.Equal(PathVerb.Close, segments[3].Verb);
        Step(segments[4], PathVerb.LineTo, 2, 1);
        Step(segments[5], PathVerb.MoveTo, 9, 9);
        Step(segments[6], PathVerb.LineTo, 8, 9);
        Assert.Equal(PathVerb.Close, segments[7].Verb);
        Step(segments[8], PathVerb.LineTo, 11, 9);
    }

    [Fact]
    public void Cubic_curves_keep_their_control_points()
    {
        var absolute = Parse("M 0 0 C 1 2 3 4 5 6")[1];
        Assert.Equal(PathVerb.CubicTo, absolute.Verb);
        At(absolute.Control1, 1, 2);
        At(absolute.Control2, 3, 4);
        At(absolute.End, 5, 6);

        var relative = Parse("M 10 10 c 1 2 3 4 5 6")[1];
        At(relative.Control1, 11, 12);
        At(relative.Control2, 13, 14);
        At(relative.End, 15, 16);
    }

    [Fact]
    public void A_smooth_cubic_mirrors_the_previous_control_point()
    {
        foreach (var data in new[] { "M 0 0 C 0 1 2 3 4 4 S 6 7 8 8", "M 0 0 C 0 1 2 3 4 4 s 2 3 4 4" })
        {
            var smooth = Parse(data)[2];

            At(smooth.Control1, 6, 5);
            At(smooth.Control2, 6, 7);
            At(smooth.End, 8, 8);
        }
    }

    [Fact]
    public void A_smooth_cubic_after_something_else_starts_at_the_current_point()
    {
        At(Parse("M 1 1 S 3 4 5 6")[1].Control1, 1, 1);
        At(Parse("M 0 0 C 1 1 2 2 3 3 L 4 4 S 6 6 7 7")[3].Control1, 4, 4);
    }

    [Fact]
    public void Quadratic_curves_become_cubic_ones()
    {
        var segment = Parse("M 0 0 Q 3 3 6 0")[1];

        Assert.Equal(PathVerb.CubicTo, segment.Verb);
        At(segment.Control1, 2, 2);
        At(segment.Control2, 4, 2);
        At(segment.End, 6, 0);
    }

    [Fact]
    public void A_smooth_quadratic_mirrors_the_previous_control_point()
    {
        var segment = Parse("M 0 0 Q 3 3 6 0 T 12 0")[2];

        At(segment.Control1, 8, -2);
        At(segment.Control2, 10, -2);
        At(segment.End, 12, 0);
    }

    [Fact]
    public void A_half_circle_is_two_curves_on_the_circle()
    {
        var segments = Parse("M 0 0 A 1 1 0 0 1 2 0");

        Assert.Equal(3, segments.Count);
        Assert.Equal(PathVerb.CubicTo, segments[1].Verb);
        Assert.Equal(PathVerb.CubicTo, segments[2].Verb);
        At(segments[1].End, 1, -1);
        At(segments[2].End, 2, 0);
        OnCircle(Sample(segments), 1, 0, 1);
    }

    [Theory]
    [InlineData(false, true, 1.7320508, -0.2679492)]
    [InlineData(false, false, -1.7320508, 0.2679492)]
    [InlineData(true, false, 1.7320508, 3.7320508)]
    [InlineData(true, true, -1.7320508, -3.7320508)]
    public void The_flags_pick_the_arc_between_two_points(bool large, bool sweep, double centreY, double apexY)
    {
        var points = Sample(Parse($"M 0 0 A 2 2 0 {(large ? 1 : 0)} {(sweep ? 1 : 0)} 2 0"));

        OnCircle(points, 1, centreY, 2);
        Assert.Contains(points, p => Math.Abs(p.X - 1) < 0.01 && Math.Abs(p.Y - apexY) < 0.01);
    }

    [Fact]
    public void Arcs_follow_ellipses_and_their_rotation()
    {
        foreach (var point in Sample(Parse("M 0 0 A 2 1 0 0 1 4 0")))
        {
            Near(1, Math.Pow((point.X - 2) / 2, 2) + point.Y * point.Y, 2e-3);
        }

        foreach (var point in Sample(Parse("M 0 0 A 2 1 90 0 1 0 4")))
        {
            Near(1, point.X * point.X + Math.Pow((point.Y - 2) / 2, 2), 2e-3);
        }
    }

    [Fact]
    public void A_relative_arc_starts_from_the_current_point()
    {
        var segments = Parse("M 1 1 a 1 1 0 0 1 2 0");

        At(segments[^1].End, 3, 1);
        OnCircle(Sample(segments), 2, 1, 1);
    }

    [Fact]
    public void Radii_too_small_for_the_two_points_are_scaled_up()
    {
        OnCircle(Sample(Parse("M 0 0 A 0.5 0.5 0 0 1 2 0")), 1, 0, 1);
    }

    [Fact]
    public void A_negative_radius_counts_as_its_absolute_value()
    {
        Assert.Equal(Parse("M 0 0 A 1 1 0 0 1 2 0"), Parse("M 0 0 A -1 -1 0 0 1 2 0"));
    }

    [Fact]
    public void An_arc_without_a_radius_is_a_straight_line()
    {
        var segments = Parse("M 0 0 A 0 5 0 0 1 3 3");

        Assert.Equal(2, segments.Count);
        Step(segments[1], PathVerb.LineTo, 3, 3);
    }

    [Fact]
    public void An_arc_to_where_it_starts_draws_nothing()
    {
        Assert.Single(Parse("M 1 1 A 2 2 0 0 1 1 1"));
    }

    [Fact]
    public void Arc_flags_can_be_glued_to_what_follows()
    {
        var spaced = Parse("M 0 0 a 1 1 0 0 1 1 0");

        Assert.Equal(spaced, Parse("M0 0a1 1 0 011 0"));
        Assert.Equal(spaced, Parse("M0,0a1,1,0,0,1,1,0"));
    }

    [Fact]
    public void Numbers_can_be_written_the_short_ways_of_svg()
    {
        var segments = Parse("M.5-.5L1.5.25 1e1-2E+1 3e-1,0");

        Step(segments[0], PathVerb.MoveTo, 0.5, -0.5);
        Step(segments[1], PathVerb.LineTo, 1.5, 0.25);
        Step(segments[2], PathVerb.LineTo, 10, -20);
        Step(segments[3], PathVerb.LineTo, 0.3, 0);
    }

    [Fact]
    public void Commas_and_any_white_space_separate_the_values()
    {
        var segments = Parse("\tM\n1\r\n2,L3,4");

        Step(segments[0], PathVerb.MoveTo, 1, 2);
        Step(segments[1], PathVerb.LineTo, 3, 4);
    }

    [Theory]
    [InlineData("L 1 1")]
    [InlineData("1 2")]
    [InlineData("M")]
    [InlineData("M 1")]
    [InlineData("M 1 x")]
    [InlineData("M 0 0 L 1")]
    [InlineData("M 0 0 L -")]
    [InlineData("M 0 0 X 1 1")]
    [InlineData("M 0 0 A 1 1 0 2 0 1 1")]
    [InlineData("M 0 0 A 1 1 0 0")]
    [InlineData("M 0 0 Z 1 2")]
    [InlineData("M 1e999 0")]
    [InlineData("M 0 0 L 1 1 ?")]
    [InlineData("M 0 0 L 1 1e")]
    public void Broken_data_is_refused(string data)
    {
        Assert.Throws<FormatException>(() => SvgPath.Parse(data));
    }

    [Fact]
    public void The_error_tells_where_the_data_breaks()
    {
        var error = Assert.Throws<FormatException>(() => SvgPath.Parse("M 0 0 L 5 oops"));

        Assert.Contains("position 10", error.Message);
    }
}

public class NavIconsTests
{
    [Fact]
    public void Every_tab_of_the_bar_has_an_icon_on_the_24_grid()
    {
        foreach (var tab in Tabs.Bar)
        {
            var icon = NavIcons.For(tab);

            Assert.NotNull(icon);
            Assert.Equal(PathVerb.MoveTo, icon.Segments[0].Verb);
            foreach (var segment in icon.Segments)
            {
                foreach (var point in new[] { segment.Control1, segment.Control2, segment.End })
                {
                    Assert.InRange(point.X, -0.5f, NavIcons.Grid + 0.5f);
                    Assert.InRange(point.Y, -0.5f, NavIcons.Grid + 0.5f);
                }
            }
        }
    }

    [Fact]
    public void Each_tab_has_its_own_drawing()
    {
        Assert.Equal(Tabs.Bar.Count, Tabs.Bar.Select(NavIcons.Data).Distinct().Count());
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("C411")]
    [InlineData("bogus")]
    [InlineData("")]
    public void Anything_but_a_tab_of_the_bar_has_no_icon(string name)
    {
        Assert.Null(NavIcons.For(name));
    }
}
