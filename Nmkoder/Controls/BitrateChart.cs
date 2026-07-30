using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nmkoder.Controls
{
    /// <summary>
    /// Minimal line chart for the bitrate-over-time view. The WinForms build used
    /// System.Windows.Forms.DataVisualization, which has no cross-platform successor, so the chart
    /// is drawn directly - it only ever needs one series, axes, and a drag selection.
    /// </summary>
    public class BitrateChart : Control
    {
        private readonly List<(long Second, int Kbps)> _points = new List<(long, int)>();

        private double? _dragStartX;
        private double? _dragCurrentX;

        // Same palette as the rest of the UI: accent line, muted axis, text that stops short of white
        private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2));
        private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(70, 0x58, 0x65, 0xF2));
        private static readonly IBrush AxisBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0x50, 0x58));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0x94, 0x9B, 0xA4));
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(56, 0x58, 0x65, 0xF2));

        private const double MarginLeft = 64;
        private const double MarginBottom = 28;
        private const double MarginTop = 12;
        private const double MarginRight = 12;

        /// <summary> Raised when the drag selection changes; carries a human-readable summary. </summary>
        public event EventHandler<string> SelectionChanged;

        public BitrateChart()
        {
            ClipToBounds = true;
        }

        public void SetData(IEnumerable<KeyValuePair<long, long>> bytesPerSecond)
        {
            _points.Clear();

            foreach (var pair in bytesPerSecond.OrderBy(x => x.Key))
                _points.Add((pair.Key, Utils.BitratePlottingUtils.BitsToKbytes(pair.Value)));

            _dragStartX = _dragCurrentX = null;
            InvalidateVisual();
        }

        public void ResetZoom()
        {
            _dragStartX = _dragCurrentX = null;
            SelectionChanged?.Invoke(this, "");
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (e.ClickCount >= 2)
            {
                ResetZoom();
                return;
            }

            _dragStartX = e.GetPosition(this).X;
            _dragCurrentX = _dragStartX;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (_dragStartX == null)
                return;

            _dragCurrentX = e.GetPosition(this).X;
            RaiseSelectionInfo();
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
            RaiseSelectionInfo();
            InvalidateVisual();
        }

        private void RaiseSelectionInfo()
        {
            if (_points.Count < 1 || _dragStartX == null || _dragCurrentX == null)
                return;

            var selected = GetSelectedPoints();

            if (selected.Count < 1)
            {
                SelectionChanged?.Invoke(this, "");
                return;
            }

            var first = selected.First();
            var last = selected.Last();
            string startStr = TimeSpan.FromSeconds(first.Second).ToString(@"hh\:mm\:ss");
            string endStr = TimeSpan.FromSeconds(last.Second).ToString(@"hh\:mm\:ss");

            if (selected.Count == 1)
            {
                SelectionChanged?.Invoke(this, $"Selected {startStr} - Bitrate: {first.Kbps}k");
                return;
            }

            SelectionChanged?.Invoke(this,
                $"Selected {startStr} to {endStr} ({selected.Count} Samples) - " +
                $"Start: {first.Kbps}k - End: {last.Kbps}k - " +
                $"Min: {selected.Min(x => x.Kbps)}k - Max: {selected.Max(x => x.Kbps)}k - Average: {selected.Average(x => x.Kbps):0}k");
        }

        private List<(long Second, int Kbps)> GetSelectedPoints()
        {
            double from = Math.Min(_dragStartX.Value, _dragCurrentX.Value);
            double to = Math.Max(_dragStartX.Value, _dragCurrentX.Value);
            Rect plot = GetPlotRect();

            if (plot.Width <= 0)
                return new List<(long, int)>();

            long minSec = _points.First().Second;
            long maxSec = _points.Last().Second;
            long span = Math.Max(1, maxSec - minSec);

            long secFrom = minSec + (long)((from - plot.X) / plot.Width * span);
            long secTo = minSec + (long)((to - plot.X) / plot.Width * span);

            return _points.Where(p => p.Second >= secFrom && p.Second <= secTo).ToList();
        }

        private Rect GetPlotRect()
        {
            double w = Math.Max(0, Bounds.Width - MarginLeft - MarginRight);
            double h = Math.Max(0, Bounds.Height - MarginTop - MarginBottom);
            return new Rect(MarginLeft, MarginTop, w, h);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Rect plot = GetPlotRect();

            if (plot.Width <= 1 || plot.Height <= 1)
                return;

            var axisPen = new Pen(AxisBrush, 1);
            context.DrawLine(axisPen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));            // Y axis
            context.DrawLine(axisPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));   // X axis

            if (_points.Count < 2)
            {
                DrawText(context, "No data", new Point(plot.X + 8, plot.Y + 8));
                return;
            }

            long minSec = _points.First().Second;
            long maxSec = _points.Last().Second;
            long spanSec = Math.Max(1, maxSec - minSec);
            int maxKbps = Math.Max(1, _points.Max(p => p.Kbps));

            // Gridlines + Y labels
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0x94, 0x9B, 0xA4)), 1);

            for (int i = 0; i <= 4; i++)
            {
                double y = plot.Bottom - plot.Height * i / 4.0;
                context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
                DrawText(context, $"{maxKbps * i / 4}k", new Point(4, y - 8));
            }

            // X labels
            for (int i = 0; i <= 4; i++)
            {
                double x = plot.X + plot.Width * i / 4.0;
                long sec = minSec + spanSec * i / 4;
                DrawText(context, TimeSpan.FromSeconds(sec).ToString(@"hh\:mm\:ss"), new Point(x - 24, plot.Bottom + 6));
            }

            // Series as a filled polygon so the shape reads at a glance
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(plot.X, plot.Bottom), true);

                foreach (var p in _points)
                    ctx.LineTo(ToScreen(p, plot, minSec, spanSec, maxKbps));

                ctx.LineTo(new Point(plot.Right, plot.Bottom));
                ctx.EndFigure(true);
            }

            context.DrawGeometry(FillBrush, new Pen(LineBrush, 1.2), geometry);

            // Drag selection overlay
            if (_dragStartX != null && _dragCurrentX != null)
            {
                double from = Math.Max(plot.X, Math.Min(_dragStartX.Value, _dragCurrentX.Value));
                double to = Math.Min(plot.Right, Math.Max(_dragStartX.Value, _dragCurrentX.Value));

                if (to > from)
                    context.FillRectangle(SelectionBrush, new Rect(from, plot.Y, to - from, plot.Height));
            }
        }

        private static Point ToScreen((long Second, int Kbps) p, Rect plot, long minSec, long spanSec, int maxKbps)
        {
            double x = plot.X + plot.Width * (p.Second - minSec) / spanSec;
            double y = plot.Bottom - plot.Height * p.Kbps / maxKbps;
            return new Point(x, y);
        }

        private static void DrawText(DrawingContext context, string text, Point origin)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, TextBrush);

            context.DrawText(formatted, origin);
        }
    }
}
