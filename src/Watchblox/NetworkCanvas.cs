using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Watchblox.Models;
using Watchblox.Services;

namespace Watchblox
{
    /// <summary>
    /// Renders a NetworkGraph as dots (friends, colored by presence) with
    /// lines between mutual friends. Pan with drag, zoom with the mouse
    /// wheel, click a dot to select it. Pan/zoom are applied through a
    /// RenderTransform so they stay smooth without redrawing.
    /// </summary>
    public class NetworkCanvas : Canvas
    {
        private const double NodeRadius = 14;

        private NetworkGraph _graph;
        private int _selected = -1;
        private HashSet<int> _selectedNeighbors = new HashSet<int>();

        private double _scale = 1, _tx, _ty;
        private bool _fitted;

        private Point? _pressPoint;
        private int _pressNode = -1;
        private bool _dragging;

        private static readonly Typeface LabelFace = new Typeface("Segoe UI");
        private static readonly Brush EdgeBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x3d, 0x40));
        private static readonly Brush EdgeHiBrush = new SolidColorBrush(Color.FromRgb(0xff, 0x3b, 0x3b));
        private static readonly Brush RingBrush = new SolidColorBrush(Color.FromRgb(0x19, 0x1b, 0x1d));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xff, 0x3b, 0x3b));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xb8, 0xbe, 0xbe));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x6b, 0x72, 0x80));

        public NetworkGraph Graph
        {
            get => _graph;
            set
            {
                _graph = value;
                _selected = -1;
                _selectedNeighbors.Clear();
                _fitted = false;
                FitToView();
                InvalidateVisual();
            }
        }

        public bool ShowLabels { get; set; } = true;

        public int SelectedIndex => _selected;
        public event Action<int> NodeSelected;

        public NetworkCanvas()
        {
            ClipToBounds = true;
            Background = Brushes.Transparent;
            Focusable = true;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (!_fitted) FitToView();
        }

        public void FitToView()
        {
            if (_graph == null || _graph.Nodes.Count == 0) return;
            if (ActualWidth < 10 || ActualHeight < 10) return;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var n in _graph.Nodes)
            {
                if (n.X < minX) minX = n.X;
                if (n.Y < minY) minY = n.Y;
                if (n.X > maxX) maxX = n.X;
                if (n.Y > maxY) maxY = n.Y;
            }
            const double pad = 40;
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;
            double bw = Math.Max(maxX - minX, 1), bh = Math.Max(maxY - minY, 1);
            // Never start more zoomed out than 0.75x — a huge graph would be
            // unreadably tiny. The graph center stays centered; pan to explore.
            _scale = Math.Clamp(Math.Min(ActualWidth / bw, ActualHeight / bh), 0.75, 2.5);
            _tx = (ActualWidth - bw * _scale) / 2 - minX * _scale;
            _ty = (ActualHeight - bh * _scale) / 2 - minY * _scale;
            _fitted = true;
            ApplyTransform();
        }

        private void ApplyTransform()
        {
            RenderTransform = new MatrixTransform(_scale, 0, 0, _scale, _tx, _ty);
        }

        private Point ToWorld(Point screen) =>
            new Point((screen.X - _tx) / _scale, (screen.Y - _ty) / _scale);

        // Mouse math runs in the canvas's PARENT coordinate space (screen pixels
        // inside the border). Measuring against the canvas itself would include
        // its own RenderTransform, which made pan speed and the zoom anchor
        // point depend on the current zoom level.
        private Point CursorPos(MouseEventArgs e)
        {
            var parent = Parent as UIElement;
            return parent != null ? e.GetPosition(parent) : e.GetPosition(this);
        }

        private int HitTest(Point screen)
        {
            if (_graph == null) return -1;
            var w = ToWorld(screen);
            int best = -1;
            double bestD = NodeRadius + 8 / _scale;
            for (int i = 0; i < _graph.Nodes.Count; i++)
            {
                var n = _graph.Nodes[i];
                double d = Math.Sqrt((n.X - w.X) * (n.X - w.X) + (n.Y - w.Y) * (n.Y - w.Y));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2)
            {
                // double-click empty space = reset view
                if (HitTest(CursorPos(e)) < 0) FitToView();
                e.Handled = true;
                return;
            }
            Focus();
            _pressPoint = CursorPos(e);
            _pressNode = HitTest(_pressPoint.Value);
            _dragging = false;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressPoint == null) return;
            var p = CursorPos(e);
            if (!_dragging)
            {
                if (Math.Abs(p.X - _pressPoint.Value.X) + Math.Abs(p.Y - _pressPoint.Value.Y) < 5)
                    return;
                _dragging = true;
            }
            _tx += p.X - _pressPoint.Value.X;
            _ty += p.Y - _pressPoint.Value.Y;
            _pressPoint = p;
            ApplyTransform();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            ReleaseMouseCapture();
            if (!_dragging && _pressNode >= 0)
                Select(_pressNode);
            _pressPoint = null;
            _dragging = false;
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double f = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            var p = CursorPos(e);
            var w = ToWorld(p);
            _scale = Math.Clamp(_scale * f, 0.1, 10);
            _tx = p.X - w.X * _scale;
            _ty = p.Y - w.Y * _scale;
            ApplyTransform();
            e.Handled = true;
        }

        private void Select(int index)
        {
            _selected = index;
            _selectedNeighbors.Clear();
            if (_graph != null && index >= 0)
                foreach (var (a, b) in _graph.Edges)
                {
                    if (a == index) _selectedNeighbors.Add(b);
                    else if (b == index) _selectedNeighbors.Add(a);
                }
            InvalidateVisual();
            NodeSelected?.Invoke(index);
        }

        public void RefreshNodes()
        {
            InvalidateVisual();
        }

        private static Color PresenceColor(PresenceType s) => s switch
        {
            PresenceType.Online => Color.FromRgb(0x22, 0xc5, 0x5e),
            PresenceType.InGame => Color.FromRgb(0x3b, 0x82, 0xf6),
            PresenceType.InStudio => Color.FromRgb(0xf5, 0x9e, 0x0b),
            _ => Color.FromRgb(0x6b, 0x72, 0x80),
        };

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (_graph == null) return;

            bool hasSel = _selected >= 0;
            // Labels turn to mush when zoomed far out — hide them there.
            bool showLabels = ShowLabels && _scale > 0.55;
            foreach (var (a, b) in _graph.Edges)
            {
                bool hi = hasSel && (a == _selected || b == _selected);
                dc.DrawLine(new Pen(hi ? EdgeHiBrush : EdgeBrush, hi ? 1.8 : 1.0),
                    new Point(_graph.Nodes[a].X, _graph.Nodes[a].Y),
                    new Point(_graph.Nodes[b].X, _graph.Nodes[b].Y));
            }

            for (int i = 0; i < _graph.Nodes.Count; i++)
            {
                var n = _graph.Nodes[i];
                var p = new Point(n.X, n.Y);
                bool dim = hasSel && i != _selected && !_selectedNeighbors.Contains(i);
                var fill = new SolidColorBrush(dim ? Color.FromRgb(0x3a, 0x3d, 0x40) : PresenceColor(n.Status));
                fill.Freeze();
                dc.DrawEllipse(fill, new Pen(RingBrush, 2), p, NodeRadius, NodeRadius);
                if (i == _selected)
                    dc.DrawEllipse(null, new Pen(AccentBrush, 2.5), p, NodeRadius + 5, NodeRadius + 5);

                if (showLabels && !string.IsNullOrEmpty(n.DisplayName))
                {
                    var ft = new FormattedText(n.DisplayName, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, LabelFace, 11,
                        dim ? DimBrush : LabelBrush, 1.0);
                    dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y + NodeRadius + 3));
                }
            }
        }
    }
}
