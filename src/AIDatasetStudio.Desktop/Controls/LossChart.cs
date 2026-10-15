using System.Globalization;
using AIDatasetStudio.Desktop.ViewModels.Training;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AIDatasetStudio.Desktop.Controls;

/// <summary>
/// The loss curves of a training run (M28): the training loss as a line, the validation loss as
/// a dashed line with dots, against the epoch. The scale fits the values drawn so far, with the
/// highest and lowest loss labelled.
/// </summary>
public sealed class LossChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> TrainPointsProperty =
        AvaloniaProperty.Register<LossChart, IReadOnlyList<ChartPoint>?>(nameof(TrainPoints));

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> EvalPointsProperty =
        AvaloniaProperty.Register<LossChart, IReadOnlyList<ChartPoint>?>(nameof(EvalPoints));

    public static readonly StyledProperty<IBrush?> TrainBrushProperty =
        AvaloniaProperty.Register<LossChart, IBrush?>(nameof(TrainBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> EvalBrushProperty =
        AvaloniaProperty.Register<LossChart, IBrush?>(nameof(EvalBrush), Brushes.DarkOrange);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<LossChart, IBrush?>(nameof(GridBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<LossChart, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    private const double LabelWidth = 52;
    private const double BottomSpace = 18;

    static LossChart()
    {
        AffectsRender<LossChart>(TrainPointsProperty, EvalPointsProperty, TrainBrushProperty, EvalBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<ChartPoint>? TrainPoints
    {
        get => GetValue(TrainPointsProperty);
        set => SetValue(TrainPointsProperty, value);
    }

    public IReadOnlyList<ChartPoint>? EvalPoints
    {
        get => GetValue(EvalPointsProperty);
        set => SetValue(EvalPointsProperty, value);
    }

    public IBrush? TrainBrush
    {
        get => GetValue(TrainBrushProperty);
        set => SetValue(TrainBrushProperty, value);
    }

    public IBrush? EvalBrush
    {
        get => GetValue(EvalBrushProperty);
        set => SetValue(EvalBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var train = TrainPoints ?? [];
        var eval = EvalPoints ?? [];
        var plot = new Rect(LabelWidth, 6, Math.Max(0, Bounds.Width - LabelWidth - 8), Math.Max(0, Bounds.Height - BottomSpace - 6));
        var grid = new Pen(GridBrush, 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + (plot.Height * i / 4);
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var all = train.Concat(eval).ToList();
        if (all.Count == 0 || plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var (minX, maxX) = (all.Min(p => p.X), all.Max(p => p.X));
        var (minY, maxY) = (all.Min(p => p.Y), all.Max(p => p.Y));

        // A flat curve still gets a band to sit in; some room above and below the extremes.
        var spread = maxY - minY;
        var pad = spread > 0 ? spread * 0.08 : Math.Max(Math.Abs(maxY) * 0.05, 0.01);
        (minY, maxY) = (minY - pad, maxY + pad);
        if (maxX <= minX)
        {
            (minX, maxX) = (minX - 0.5, maxX + 0.5);
        }

        Point At(ChartPoint p) => new(
            plot.Left + (plot.Width * (p.X - minX) / (maxX - minX)),
            plot.Bottom - (plot.Height * (p.Y - minY) / (maxY - minY)));

        DrawCurve(context, train, At, new Pen(TrainBrush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), dots: train.Count == 1);
        DrawCurve(context, eval, At, new Pen(EvalBrush, 2, dashStyle: new DashStyle([3, 2], 0), lineJoin: PenLineJoin.Round), dots: true);

        Label(context, (maxY - pad).ToString("0.000", CultureInfo.CurrentCulture), new Point(0, At(new ChartPoint(minX, maxY - pad)).Y - 8));
        Label(context, (minY + pad).ToString("0.000", CultureInfo.CurrentCulture), new Point(0, At(new ChartPoint(minX, minY + pad)).Y - 8));
        Label(context, minX.ToString("0.##", CultureInfo.CurrentCulture), new Point(plot.Left, plot.Bottom + 2));
        var last = maxX.ToString("0.##", CultureInfo.CurrentCulture);
        Label(context, last, new Point(plot.Right - (7 * last.Length), plot.Bottom + 2));
    }

    private void DrawCurve(DrawingContext context, IReadOnlyList<ChartPoint> points, Func<ChartPoint, Point> at, Pen pen, bool dots)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var figure = geometry.Open())
            {
                figure.BeginFigure(at(points[0]), isFilled: false);
                foreach (var point in points.Skip(1))
                {
                    figure.LineTo(at(point));
                }

                figure.EndFigure(isClosed: false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        if (dots)
        {
            foreach (var point in points)
            {
                context.DrawEllipse(pen.Brush, null, at(point), 3, 3);
            }
        }
    }

    private void Label(DrawingContext context, string text, Point origin)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, LabelBrush);
        context.DrawText(formatted, new Point(Math.Max(0, origin.X), Math.Max(0, origin.Y)));
    }
}
