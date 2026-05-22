using System.Collections.Generic;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Drawing.Layouts;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Drawing.Layouts;
using LiveChartsCore.SkiaSharpView.SKCharts;

namespace CreatioHelper;

public class PipelineTooltip : SKDefaultTooltip
{
    protected override void Initialize(Chart chart)
    {
        base.Initialize(chart);
        Wedge = 0;
        Geometry.Wedge = 0;
    }

    public override void Show(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        var points = foundPoints.ToList();
        base.Show(points, chart);

        if (points.Count > 0 && points[0].Context.HoverArea is RectangleHoverArea rect)
        {
            var size = Measure();

            var cy = rect.Y + rect.Height * 0.5f - size.Height * 0.5f;
            var cx = rect.X + rect.Width * 0.5f - size.Width * 0.5f;

            if (cy < 0) cy = 0;
            if (cy + size.Height > chart.ControlSize.Height) cy = chart.ControlSize.Height - size.Height;
            if (cx < 0) cx = 0;
            if (cx + size.Width > chart.ControlSize.Width) cx = chart.ControlSize.Width - size.Width;

            X = cx;
            Y = cy;

            chart.Canvas.Invalidate();
        }
    }

    protected override Layout<SkiaSharpDrawingContext> GetLayout(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        var theme = chart.GetTheme();

        var layout = new StackLayout
        {
            Padding = new Padding(10),
            Orientation = ContainerOrientation.Vertical,
            HorizontalAlignment = Align.Middle,
            VerticalAlignment = Align.Middle
        };

        foreach (var point in foundPoints)
        {
            var v = point.Coordinate.PrimaryValue;
            if (v <= 0) continue;

            var series = point.Context.Series;
            var formatted = DurationFormatter.Format(v);

            var miniature = (IDrawnElement<SkiaSharpDrawingContext>)series.GetMiniatureGeometry(point);

            var label = new LabelGeometry
            {
                Text = $"{series.Name}  {formatted}",
                Paint = theme.TooltipTextPaint,
                TextSize = 13,
                Padding = new Padding(8, 0),
                VerticalAlign = Align.Start,
                HorizontalAlign = Align.Start
            };

            var row = new StackLayout
            {
                Orientation = ContainerOrientation.Horizontal,
                Padding = new Padding(0, 2),
                VerticalAlignment = Align.Middle,
                HorizontalAlignment = Align.Middle,
                Children = { miniature, label }
            };

            layout.Children.Add(row);
        }

        return layout;
    }
}
