using System.Drawing;
using System.Globalization;

namespace Arr411.Core.Drawing;

public enum PathVerb
{
    /// <summary>Start a new figure at <see cref="PathSegment.End"/>.</summary>
    MoveTo,

    /// <summary>A straight line from the current point to <see cref="PathSegment.End"/>.</summary>
    LineTo,

    /// <summary>A cubic Bézier curve from the current point, with the two control points and the end point.</summary>
    CubicTo,

    /// <summary>A line back to the start of the figure, which is closed.</summary>
    Close,
}

/// <summary>One drawing step, in absolute coordinates. Unused points are left at zero.</summary>
public readonly record struct PathSegment(PathVerb Verb, PointF Control1, PointF Control2, PointF End);

/// <summary>
/// An SVG path (the <c>d</c> attribute, or the <c>pathData</c> of an Android vector drawable) reduced to lines and cubic Bézier curves,
/// which is all that GDI+ draws. Quadratic curves and elliptical arcs are converted.
/// </summary>
public sealed class SvgPath
{
    private SvgPath(IReadOnlyList<PathSegment> segments) => Segments = segments;

    public IReadOnlyList<PathSegment> Segments { get; }

    /// <exception cref="FormatException">The data is not a valid path.</exception>
    public static SvgPath Parse(string data) => new(new Parser(data).Run());

    private enum Control
    {
        None,
        Cubic,
        Quadratic,
    }

    private sealed class Parser(string text)
    {
        private readonly List<PathSegment> _segments = [];
        private int _pos;
        private double _x;
        private double _y;
        private double _startX;
        private double _startY;
        private double _controlX;
        private double _controlY;
        private Control _control;

        public List<PathSegment> Run()
        {
            var command = '\0';
            var first = true;
            while (true)
            {
                SkipSeparators();
                if (_pos >= text.Length) break;

                if (char.IsAsciiLetter(text[_pos]))
                {
                    command = text[_pos++];
                }
                else if (command is '\0' or 'Z' or 'z')
                {
                    throw Error("a command was expected");
                }
                else if (command == 'M')
                {
                    command = 'L';
                }
                else if (command == 'm')
                {
                    command = 'l';
                }

                if (first && command is not ('M' or 'm')) throw Error("a path starts with a moveto");

                first = false;
                Apply(command);
            }

            return _segments;
        }

        private void Apply(char command)
        {
            var relative = char.IsAsciiLetterLower(command);
            var ox = relative ? _x : 0;
            var oy = relative ? _y : 0;
            var control = Control.None;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    MoveTo(Number() + ox, Number() + oy);
                    break;
                case 'L':
                    LineTo(Number() + ox, Number() + oy);
                    break;
                case 'H':
                    LineTo(Number() + ox, _y);
                    break;
                case 'V':
                    LineTo(_x, Number() + oy);
                    break;
                case 'C':
                {
                    var (x1, y1) = (Number() + ox, Number() + oy);
                    var (x2, y2) = (Number() + ox, Number() + oy);
                    CubicTo(x1, y1, x2, y2, Number() + ox, Number() + oy);
                    control = Control.Cubic;
                    (_controlX, _controlY) = (x2, y2);
                    break;
                }

                case 'S':
                {
                    var (x1, y1) = _control == Control.Cubic ? (2 * _x - _controlX, 2 * _y - _controlY) : (_x, _y);
                    var (x2, y2) = (Number() + ox, Number() + oy);
                    CubicTo(x1, y1, x2, y2, Number() + ox, Number() + oy);
                    control = Control.Cubic;
                    (_controlX, _controlY) = (x2, y2);
                    break;
                }

                case 'Q':
                {
                    var (qx, qy) = (Number() + ox, Number() + oy);
                    QuadraticTo(qx, qy, Number() + ox, Number() + oy);
                    control = Control.Quadratic;
                    (_controlX, _controlY) = (qx, qy);
                    break;
                }

                case 'T':
                {
                    var (qx, qy) = _control == Control.Quadratic ? (2 * _x - _controlX, 2 * _y - _controlY) : (_x, _y);
                    QuadraticTo(qx, qy, Number() + ox, Number() + oy);
                    control = Control.Quadratic;
                    (_controlX, _controlY) = (qx, qy);
                    break;
                }

                case 'A':
                {
                    var rx = Number();
                    var ry = Number();
                    var rotation = Number();
                    var large = Flag();
                    var sweep = Flag();
                    ArcTo(rx, ry, rotation, large, sweep, Number() + ox, Number() + oy);
                    break;
                }

                case 'Z':
                    _segments.Add(new PathSegment(PathVerb.Close, default, default, default));
                    (_x, _y) = (_startX, _startY);
                    break;
                default:
                    throw Error($"unknown command '{command}'");
            }

            _control = control;
        }

        private void MoveTo(double x, double y)
        {
            _segments.Add(Segment(PathVerb.MoveTo, 0, 0, 0, 0, x, y));
            (_x, _y) = (x, y);
            (_startX, _startY) = (x, y);
        }

        private void LineTo(double x, double y)
        {
            _segments.Add(Segment(PathVerb.LineTo, 0, 0, 0, 0, x, y));
            (_x, _y) = (x, y);
        }

        private void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
        {
            _segments.Add(Segment(PathVerb.CubicTo, x1, y1, x2, y2, x, y));
            (_x, _y) = (x, y);
        }

        private void QuadraticTo(double qx, double qy, double x, double y) =>
            CubicTo(_x + 2.0 / 3 * (qx - _x), _y + 2.0 / 3 * (qy - _y), x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y);

        // The conversion of SVG 1.1 "Implementation notes", F.6.5 (endpoint to centre parameterization), then one Bézier curve per quarter turn.
        private void ArcTo(double rx, double ry, double rotation, bool large, bool sweep, double x, double y)
        {
            var (x0, y0) = (_x, _y);
            if (x0 == x && y0 == y) return;

            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx == 0 || ry == 0)
            {
                LineTo(x, y);
                return;
            }

            var (sin, cos) = Math.SinCos(rotation * Math.PI / 180);
            var dx = (x0 - x) / 2;
            var dy = (y0 - y) / 2;
            var x1 = cos * dx + sin * dy;
            var y1 = -sin * dx + cos * dy;

            // Radii too small to span the two points are scaled up until they do.
            var lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1)
            {
                var scale = Math.Sqrt(lambda);
                rx *= scale;
                ry *= scale;
            }

            var numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            var denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            var factor = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
            if (large == sweep) factor = -factor;

            var cx1 = factor * rx * y1 / ry;
            var cy1 = -factor * ry * x1 / rx;
            var cx = cos * cx1 - sin * cy1 + (x0 + x) / 2;
            var cy = sin * cx1 + cos * cy1 + (y0 + y) / 2;

            var first = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
            var last = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
            var delta = last - first;
            if (sweep && delta < 0) delta += 2 * Math.PI;
            else if (!sweep && delta > 0) delta -= 2 * Math.PI;

            var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
            var step = delta / count;
            var kappa = 4.0 / 3 * Math.Tan(step / 4);

            (double X, double Y) Point(double angle)
            {
                var (s, c) = Math.SinCos(angle);
                return (cx + cos * rx * c - sin * ry * s, cy + sin * rx * c + cos * ry * s);
            }

            (double X, double Y) Tangent(double angle)
            {
                var (s, c) = Math.SinCos(angle);
                return (-cos * rx * s - sin * ry * c, -sin * rx * s + cos * ry * c);
            }

            for (var i = 0; i < count; i++)
            {
                var a0 = first + i * step;
                var a1 = a0 + step;
                var (p0, t0) = (Point(a0), Tangent(a0));
                var (p1, t1) = (Point(a1), Tangent(a1));
                var final = i == count - 1;
                CubicTo(p0.X + kappa * t0.X, p0.Y + kappa * t0.Y, p1.X - kappa * t1.X, p1.Y - kappa * t1.Y, final ? x : p1.X, final ? y : p1.Y);
            }
        }

        private static PathSegment Segment(PathVerb verb, double x1, double y1, double x2, double y2, double x, double y) =>
            new(verb, new PointF((float)x1, (float)y1), new PointF((float)x2, (float)y2), new PointF((float)x, (float)y));

        private double Number()
        {
            SkipSeparators();
            var start = _pos;
            if (_pos < text.Length && text[_pos] is '+' or '-') _pos++;

            var digits = Digits();
            if (_pos < text.Length && text[_pos] == '.')
            {
                _pos++;
                digits += Digits();
            }

            if (digits == 0)
            {
                _pos = start;
                throw Error("a number was expected");
            }

            if (_pos < text.Length && text[_pos] is 'e' or 'E')
            {
                var mark = _pos++;
                if (_pos < text.Length && text[_pos] is '+' or '-') _pos++;
                if (Digits() == 0) _pos = mark;
            }

            var value = double.Parse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value)) throw Error("a number is out of range");

            return value;
        }

        private int Digits()
        {
            var start = _pos;
            while (_pos < text.Length && char.IsAsciiDigit(text[_pos])) _pos++;
            return _pos - start;
        }

        // An arc flag is a single 0 or 1 that may be glued to the number after it ("a1 1 0 011 1").
        private bool Flag()
        {
            SkipSeparators();
            if (_pos >= text.Length || text[_pos] is not ('0' or '1')) throw Error("an arc flag (0 or 1) was expected");

            return text[_pos++] == '1';
        }

        private void SkipSeparators()
        {
            while (_pos < text.Length && (char.IsWhiteSpace(text[_pos]) || text[_pos] == ',')) _pos++;
        }

        private FormatException Error(string message) => new($"Invalid SVG path: {message} (at position {_pos}).");
    }
}
