// MU/deg + Superposed MU/deg (VMAT/Arc quick-view)
// - Arc range is per-beam (based on meterset change)
// - Optional shared Y-axis scaling (default OFF)
// - Avoidance-like dead segments are marked with faint red dashed lines

using System;
using System.Collections.Generic;
using System.Linq;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace VMS.TPS
{
    public class Script
    {
        private const int X_TICK_STEP_DEG = 30;
        private const double MW_EPS = 1e-6;

        // Right-panel sizing (scaled up 1.5x to reduce label overlap)
        private const int RIGHT_POLAR_W = 672;  // 448 * 1.5
        private const int RIGHT_POLAR_H = 504;  // 336 * 1.5

        public void Execute(ScriptContext context)
        {
            ExternalPlanSetup plan = GetCurrentExternalPlan(context);
            if (plan == null)
            {
                MessageBox.Show("No ExternalPlanSetup is currently open.\nOpen an external beam plan and try again.");
                return;
            }

            List<Beam> arcBeams = plan.Beams
                .Where(b => b != null && !b.IsSetupField)
                .Where(IsArcLikeBeam)
                .ToList();

            if (arcBeams.Count == 0)
            {
                MessageBox.Show("No arc/VMAT beams found in the current plan.");
                return;
            }

            List<BeamRaw> beams = new List<BeamRaw>();

            foreach (var beam in arcBeams)
            {
                if (beam.ControlPoints == null || beam.ControlPoints.Count < 2) continue;

                double totalMU = GetBeamTotalMU(beam);
                if (totalMU <= 0.0) continue;

                GantryDirection dir = GetBeamDirectionSafe(beam);

                int activeStartDeg, activeEndDeg;
                bool hasActive = GetActiveArcEndpointsFromMeterset(beam, dir, out activeStartDeg, out activeEndDeg);

                if (!hasActive)
                {
                    activeStartDeg = (int)Math.Round(NormalizeAngle(beam.ControlPoints[0].GantryAngle));
                    activeEndDeg = (int)Math.Round(NormalizeAngle(beam.ControlPoints[beam.ControlPoints.Count - 1].GantryAngle));
                }

                List<int> displayPath = BuildAnglePath_1Deg(activeStartDeg, activeEndDeg, dir);
                if (displayPath == null || displayPath.Count < 2) continue;

                List<double> sSamples, ySamples;
                BuildSamplesFromControlPoints(beam, totalMU, dir, out sSamples, out ySamples);
                if (sSamples == null || ySamples == null || sSamples.Count < 2 || ySamples.Count != sSamples.Count)
                    continue;

                beams.Add(new BeamRaw
                {
                    BeamId = beam.Id,
                    Direction = dir,
                    SSamples = sSamples,
                    YSamples = ySamples,
                    DisplayPathAngles = displayPath
                });
            }

            if (beams.Count == 0)
            {
                MessageBox.Show("No usable beams (check meterset/control points).");
                return;
            }

            Window w = BuildMainWindow(plan, beams);
            w.ShowDialog();
        }

        private Window BuildMainWindow(ExternalPlanSetup plan, List<BeamRaw> beams)
        {
            Window w = new Window();
            w.Title = "MU/deg + Superposed MU/deg - " + plan.Course.Patient.Id + " | " + plan.Id;
            w.Width = 1500;
            w.Height = 850;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Dictionary<string, bool> beamEnabled = new Dictionary<string, bool>();
            foreach (var br in beams)
                beamEnabled[br.BeamId] = true;

            bool isRendering = false;

            DockPanel root = new DockPanel();

            StackPanel topBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };

            TextBlock lbl = new TextBlock
            {
                Text = "Interpolation:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            ComboBox cb = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 12, 0) };
            cb.Items.Add("Linear");
            cb.Items.Add("Quadratic");
            cb.SelectedIndex = 1;

            CheckBox chkSameY = new CheckBox
            {
                Content = "Use same vertical axis range",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                IsChecked = false
            };

            TextBlock hint = new TextBlock
            {
                Text = "(Dynamic arc range per beam; per-beam Y autoscale by default)",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            };

            topBar.Children.Add(lbl);
            topBar.Children.Add(cb);
            topBar.Children.Add(chkSameY);
            topBar.Children.Add(hint);

            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            Grid main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            root.Children.Add(main);

            ScrollViewer svLeft = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10) };
            Grid.SetColumn(svLeft, 0);
            main.Children.Add(svLeft);

            StackPanel leftStack = new StackPanel();
            svLeft.Content = leftStack;

            Border rightBox = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(10),
                CornerRadius = new CornerRadius(4)
            };
            Grid.SetColumn(rightBox, 1);
            main.Children.Add(rightBox);

            StackPanel rightStack = new StackPanel();
            rightBox.Child = rightStack;

            TextBlock rt = new TextBlock { FontSize = 14, Text = "Superposed MU/deg", Margin = new Thickness(0, 0, 0, 6) };
            rightStack.Children.Add(rt);

            TextBlock stats = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
            rightStack.Children.Add(stats);

            Canvas polarCanvas = new Canvas();
            rightStack.Children.Add(polarCanvas);

            Action render = null;

            render = () =>
            {
                if (isRendering) return;
                isRendering = true;

                try
                {
                    string method = (cb.SelectedItem != null) ? cb.SelectedItem.ToString() : "Quadratic";
                    bool useSameY = (chkSameY.IsChecked == true);

                    leftStack.Children.Clear();

                    double[] combinedBins = new double[360];
                    List<BeamComputed> computedSeries = new List<BeamComputed>();

                    double globalMax = 0.0;

                    foreach (var br in beams)
                    {
                        int N = br.DisplayPathAngles.Count;

                        List<double> yPerStep = ResampleTo1Deg(br.SSamples, br.YSamples, N, method);

                        List<double> yAlongDisplay = new List<double>(N);
                        for (int k = 0; k < N; k++)
                            yAlongDisplay.Add(SafeMu(yPerStep[k]));

                        List<double> vals = yAlongDisplay.Select(SafeMu).ToList();
                        double bMax = (vals.Count > 0) ? vals.Max() : 0.0;
                        double bMin = (vals.Count > 0) ? vals.Min() : 0.0;
                        double bMean = (vals.Count > 0) ? Mean(vals) : 0.0;
                        double bStd = (vals.Count > 0) ? StdDev(vals) : 0.0;

                        if (bMax > globalMax) globalMax = bMax;

                        List<AvoidRange> avoidRanges = DetectAvoidanceLikeRuns(
                            br.DisplayPathAngles,
                            yAlongDisplay,
                            eps: 1e-9,
                            minRunLen: 2
                        );

                        bool enabled = beamEnabled.ContainsKey(br.BeamId) ? beamEnabled[br.BeamId] : true;
                        if (enabled)
                        {
                            for (int k = 0; k < N; k++)
                            {
                                int ang = br.DisplayPathAngles[k];
                                combinedBins[ang] += yAlongDisplay[k];
                            }
                        }

                        computedSeries.Add(new BeamComputed
                        {
                            BeamId = br.BeamId,
                            Direction = br.Direction,
                            Angles = br.DisplayPathAngles,
                            Values = yAlongDisplay,
                            Max = bMax,
                            Min = bMin,
                            Mean = bMean,
                            Std = bStd,
                            AvoidRanges = avoidRanges
                        });
                    }

                    if (globalMax <= 1e-12) globalMax = 1.0;
                    double sharedYAxisMax = globalMax * 1.10;

                    foreach (var s in computedSeries)
                    {
                        Border box = new Border
                        {
                            BorderBrush = Brushes.LightGray,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(10),
                            Margin = new Thickness(0, 0, 0, 12),
                            CornerRadius = new CornerRadius(4)
                        };

                        StackPanel block = new StackPanel();
                        DockPanel titleRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

                        CheckBox chk = new CheckBox
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 10, 0),
                            IsChecked = beamEnabled.ContainsKey(s.BeamId) ? (bool?)beamEnabled[s.BeamId] : true,
                            ToolTip = "Include this beam in the superposed MU/deg plot on the right"
                        };

                        chk.Checked += (sender, e) =>
                        {
                            if (isRendering) return;
                            beamEnabled[s.BeamId] = true;
                            if (render != null) render();
                        };

                        chk.Unchecked += (sender, e) =>
                        {
                            if (isRendering) return;
                            beamEnabled[s.BeamId] = false;
                            if (render != null) render();
                        };

                        string avoidText = (s.AvoidRanges != null && s.AvoidRanges.Count > 0)
                            ? ("Arc avoidance: " + FormatAvoidRanges(s.AvoidRanges))
                            : "No arc avoidance";

                        TextBlock title = new TextBlock
                        {
                            FontSize = 14,
                            Text =
                                "Beam: " + s.BeamId
                                + "   Dir=" + (s.Direction == GantryDirection.CounterClockwise ? "CCW" : "CW")
                                + "   " + avoidText
                                + "   max=" + s.Max.ToString("0.###")
                                + "   min=" + s.Min.ToString("0.###")
                                + "   mean=" + s.Mean.ToString("0.###")
                                + "   std=" + s.Std.ToString("0.###")
                        };

                        DockPanel.SetDock(chk, Dock.Left);
                        titleRow.Children.Add(chk);
                        titleRow.Children.Add(title);

                        double yAxisMax = useSameY ? sharedYAxisMax : ((s.Max <= 1e-12) ? 1.0 : s.Max * 1.10);

                        Canvas plot = BuildLeftPerDegreePlot(
                            s.Angles,
                            s.Values,
                            760,
                            165,
                            yAxisMax,
                            X_TICK_STEP_DEG,
                            s.AvoidRanges
                        );

                        block.Children.Add(titleRow);
                        block.Children.Add(plot);
                        box.Child = block;

                        leftStack.Children.Add(box);
                    }

                    double[] combinedSafe = new double[360];
                    for (int d = 0; d < 360; d++) combinedSafe[d] = SafeMu(combinedBins[d]);

                    combinedSafe[180] = 0.5 * (combinedSafe[179] + combinedSafe[181]);

                    double cMax = combinedSafe.Max();
                    double cMin = combinedSafe.Min();
                    double cMean = Mean(combinedSafe);
                    double cStd = StdDev(combinedSafe);

                    // 2 dp on the right stats line
                    stats.Text = "max=" + cMax.ToString("0.00")
                              + "   min=" + cMin.ToString("0.00")
                              + "   mean=" + cMean.ToString("0.00")
                              + "   std=" + cStd.ToString("0.00");

                    rightStack.Children.Remove(polarCanvas);
                    PolarScale sc;
                    polarCanvas = BuildPolar_ZeroOnCircle_Sum(combinedSafe, RIGHT_POLAR_W, RIGHT_POLAR_H, out sc);
                    rightStack.Children.Add(polarCanvas);
                }
                finally
                {
                    isRendering = false;
                }
            };

            render();
            cb.SelectionChanged += (s, e) => { if (render != null) render(); };
            chkSameY.Checked += (s, e) => { if (render != null) render(); };
            chkSameY.Unchecked += (s, e) => { if (render != null) render(); };

            w.Content = root;
            return w;
        }

        private static bool GetActiveArcEndpointsFromMeterset(Beam beam, GantryDirection dir, out int startDeg, out int endDeg)
        {
            startDeg = 0;
            endDeg = 0;

            try
            {
                var cps = beam.ControlPoints;
                if (cps == null || cps.Count < 2) return false;

                bool found = false;
                int firstFrom = 0;
                int lastTo = 0;

                for (int i = 1; i < cps.Count; i++)
                {
                    double mw0 = cps[i - 1].MetersetWeight;
                    double mw1 = cps[i].MetersetWeight;

                    if (!IsFinite(mw0) || !IsFinite(mw1)) continue;

                    double dMW = mw1 - mw0;
                    if (!IsFinite(dMW)) continue;

                    if (dMW > MW_EPS)
                    {
                        int a0 = (int)Math.Round(NormalizeAngle(cps[i - 1].GantryAngle));
                        int a1 = (int)Math.Round(NormalizeAngle(cps[i].GantryAngle));

                        if (!found)
                        {
                            firstFrom = a0;
                            lastTo = a1;
                            found = true;
                        }
                        else
                        {
                            lastTo = a1;
                        }
                    }
                }

                if (!found) return false;

                startDeg = firstFrom;
                endDeg = lastTo;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<double> ResampleTo1Deg(List<double> sSamples, List<double> ySamples, int N, string method)
        {
            List<double> outY = new List<double>(N);

            if (sSamples == null || ySamples == null || sSamples.Count < 2 || ySamples.Count != sSamples.Count)
            {
                for (int i = 0; i < N; i++) outY.Add(0.0);
                return outY;
            }

            for (int k = 0; k < N; k++)
            {
                double x = k;
                double v = (method == "Quadratic" && sSamples.Count >= 3)
                    ? QuadraticInterp(sSamples, ySamples, x)
                    : LinearInterp(sSamples, ySamples, x);

                outY.Add(v);
            }

            return outY;
        }

        private static double LinearInterp(List<double> xs, List<double> ys, double x)
        {
            int n = xs.Count;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];

            int j = 1;
            while (j < n && xs[j] < x) j++;

            int i = j - 1;
            double x0 = xs[i], x1 = xs[j];
            double y0 = ys[i], y1 = ys[j];

            double t = (x1 - x0) > 1e-12 ? (x - x0) / (x1 - x0) : 0.0;
            return y0 + t * (y1 - y0);
        }

        private static double QuadraticInterp(List<double> xs, List<double> ys, double x)
        {
            int n = xs.Count;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];

            int j = 1;
            while (j < n && xs[j] < x) j++;

            int i1 = j;
            int i0 = i1 - 1;
            int i2 = i1 + 1;

            if (i2 >= n)
            {
                i2 = n - 1;
                i1 = i2 - 1;
                i0 = i1 - 1;
            }
            if (i0 < 0)
            {
                i0 = 0;
                i1 = 1;
                i2 = 2;
            }

            double x0 = xs[i0], x1 = xs[i1], x2 = xs[i2];
            double y0 = ys[i0], y1 = ys[i1], y2 = ys[i2];

            double d0 = (x0 - x1) * (x0 - x2);
            double d1 = (x1 - x0) * (x1 - x2);
            double d2 = (x2 - x0) * (x2 - x1);

            if (Math.Abs(d0) < 1e-12 || Math.Abs(d1) < 1e-12 || Math.Abs(d2) < 1e-12)
                return LinearInterp(xs, ys, x);

            double L0 = ((x - x1) * (x - x2)) / d0;
            double L1 = ((x - x0) * (x - x2)) / d1;
            double L2 = ((x - x0) * (x - x1)) / d2;

            return y0 * L0 + y1 * L1 + y2 * L2;
        }

        private static void BuildSamplesFromControlPoints(Beam beam, double totalMU, GantryDirection dir,
            out List<double> sSamples, out List<double> ySamples)
        {
            sSamples = new List<double>();
            ySamples = new List<double>();

            var cps = beam.ControlPoints;
            double sCum = 0.0;

            bool haveFirst = false;
            double firstY = 0.0;

            for (int i = 1; i < cps.Count; i++)
            {
                ControlPoint prev = cps[i - 1];
                ControlPoint cur = cps[i];

                double a0 = NormalizeAngle(prev.GantryAngle);
                double a1 = NormalizeAngle(cur.GantryAngle);

                double dDeg = ForwardDeltaDegrees(a0, a1, dir);
                if (dDeg < 1e-9) continue;

                double mw0 = prev.MetersetWeight;
                double mw1 = cur.MetersetWeight;
                if (!IsFinite(mw0) || !IsFinite(mw1)) continue;

                double dMW = mw1 - mw0;
                if (!IsFinite(dMW)) continue;
                if (dMW < 0) dMW = 0;

                double muSeg = dMW * totalMU;
                double muPerDeg = muSeg / dDeg;
                if (!IsFinite(muPerDeg)) continue;

                if (!haveFirst)
                {
                    firstY = muPerDeg;
                    haveFirst = true;
                }

                sCum += dDeg;
                sSamples.Add(sCum);
                ySamples.Add(muPerDeg);
            }

            if (sSamples.Count > 0)
            {
                sSamples.Insert(0, 0.0);
                ySamples.Insert(0, haveFirst ? firstY : 0.0);
            }
        }

        private static double Mean(IEnumerable<double> xs)
        {
            double sum = 0.0;
            int n = 0;
            foreach (var v in xs) { sum += v; n++; }
            return (n > 0) ? (sum / n) : 0.0;
        }

        private static double StdDev(IEnumerable<double> xs)
        {
            List<double> a = xs.ToList();
            int n = a.Count;
            if (n <= 0) return 0.0;
            double m = Mean(a);
            double s2 = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = a[i] - m;
                s2 += d * d;
            }
            return Math.Sqrt(s2 / n);
        }

        private struct AvoidRange
        {
            public int StartIdx;
            public int EndIdx;
            public int StartAngle;
            public int EndAngle;
        }

        private static List<AvoidRange> DetectAvoidanceLikeRuns(
            List<int> angles,
            List<double> values,
            double eps = 1e-9,
            int minRunLen = 2)
        {
            List<AvoidRange> ranges = new List<AvoidRange>();
            if (angles == null || values == null || angles.Count != values.Count || angles.Count < 2) return ranges;

            int n = angles.Count;
            int i = 0;
            while (i < n)
            {
                bool isZero = SafeMu(values[i]) <= eps;
                if (!isZero) { i++; continue; }

                int j = i;
                while (j + 1 < n && SafeMu(values[j + 1]) <= eps) j++;

                int runLen = (j - i + 1);
                if (runLen >= minRunLen)
                {
                    ranges.Add(new AvoidRange
                    {
                        StartIdx = i,
                        EndIdx = j,
                        StartAngle = angles[i],
                        EndAngle = angles[j]
                    });
                }

                i = j + 1;
            }

            return ranges;
        }

        private static string FormatAvoidRanges(List<AvoidRange> ranges)
        {
            if (ranges == null || ranges.Count == 0) return "";
            List<string> parts = new List<string>();
            for (int i = 0; i < ranges.Count; i++)
                parts.Add(ranges[i].StartAngle.ToString() + "–" + ranges[i].EndAngle.ToString());
            return string.Join(", ", parts);
        }

        private static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

        private static double SafeMu(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0.0) return 0.0;
            return v;
        }

        private static ExternalPlanSetup GetCurrentExternalPlan(ScriptContext context)
        {
            if (context.ExternalPlanSetup != null) return context.ExternalPlanSetup;
            return context.PlanSetup as ExternalPlanSetup;
        }

        private static bool IsArcLikeBeam(Beam b)
        {
            if (b == null) return false;
            if (b.ControlPoints == null || b.ControlPoints.Count < 2) return false;
            if (b.GantryDirection != GantryDirection.None) return true;

            double a0 = b.ControlPoints[0].GantryAngle;
            double a1 = b.ControlPoints[b.ControlPoints.Count - 1].GantryAngle;
            return Math.Abs(NormalizeAngle(a1) - NormalizeAngle(a0)) > 0.01;
        }

        private static GantryDirection GetBeamDirectionSafe(Beam beam)
        {
            GantryDirection dir = beam.GantryDirection;
            if (dir == GantryDirection.None)
            {
                var cps = beam.ControlPoints;
                dir = InferDirectionFromCPs(cps[0].GantryAngle, cps[1].GantryAngle);
            }
            return dir;
        }

        private static GantryDirection InferDirectionFromCPs(double a0, double a1)
        {
            double from = NormalizeAngle(a0);
            double to = NormalizeAngle(a1);

            double dec = from - to; if (dec < 0) dec += 360;
            double inc = to - from; if (inc < 0) inc += 360;

            return (dec <= inc) ? GantryDirection.CounterClockwise : GantryDirection.Clockwise;
        }

        private static double NormalizeAngle(double deg)
        {
            deg = deg % 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        private static double GetBeamTotalMU(Beam beam)
        {
            try { return beam.Meterset.Value; }
            catch { return 0.0; }
        }

        private static double ForwardDeltaDegrees(double from, double to, GantryDirection dir)
        {
            from = NormalizeAngle(from);
            to = NormalizeAngle(to);

            if (dir == GantryDirection.CounterClockwise)
            {
                double d = from - to;
                if (d < 0) d += 360;
                return d;
            }
            else
            {
                double d = to - from;
                if (d < 0) d += 360;
                return d;
            }
        }

        private static List<int> BuildAnglePath_1Deg(int start, int end, GantryDirection dir)
        {
            start = ((start % 360) + 360) % 360;
            end = ((end % 360) + 360) % 360;

            List<int> path = new List<int>();
            path.Add(start);

            int cur = start;
            int guard = 0;

            if (dir == GantryDirection.CounterClockwise)
            {
                while (cur != end && guard < 360)
                {
                    cur = (cur + 359) % 360;
                    path.Add(cur);
                    guard++;
                }
            }
            else
            {
                while (cur != end && guard < 360)
                {
                    cur = (cur + 1) % 360;
                    path.Add(cur);
                    guard++;
                }
            }

            return path;
        }

        private static Canvas BuildLeftPerDegreePlot(
            List<int> angles,
            List<double> y,
            double width,
            double height,
            double yAxisMax,
            int tickStep,
            List<AvoidRange> avoidRanges)
        {
            Canvas c = new Canvas();
            c.Width = width;
            c.Height = height;
            c.Background = Brushes.White;

            if (angles == null || y == null || angles.Count < 2 || y.Count != angles.Count)
                return c;

            List<double> ySafe = y.Select(SafeMu).ToList();

            double left = 85;
            double right = 20;
            double top = 12;
            double bottom = 50;

            double plotW = width - left - right;
            double plotH = height - top - bottom;

            double ymin = 0.0;
            double ymax = yAxisMax;
            if (ymax <= 1e-12) ymax = 1.0;
            double yr = ymax - ymin;

            Line xAxis = new Line { X1 = left, Y1 = top + plotH, X2 = left + plotW, Y2 = top + plotH, Stroke = Brushes.Black, StrokeThickness = 1 };
            Line yAxis = new Line { X1 = left, Y1 = top, X2 = left, Y2 = top + plotH, Stroke = Brushes.Black, StrokeThickness = 1 };
            c.Children.Add(xAxis);
            c.Children.Add(yAxis);

            int yTicks = 5;
            for (int t = 0; t < yTicks; t++)
            {
                double frac = t / (double)(yTicks - 1);
                double val = ymin + frac * yr;
                double yy = top + plotH - frac * plotH;

                Line grid = new Line { X1 = left, Y1 = yy, X2 = left + plotW, Y2 = yy, Stroke = Brushes.LightGray, StrokeThickness = 1 };
                Line tick = new Line { X1 = left - 4, Y1 = yy, X2 = left, Y2 = yy, Stroke = Brushes.Black, StrokeThickness = 1 };
                c.Children.Add(grid);
                c.Children.Add(tick);

                TextBlock lab = new TextBlock
                {
                    FontSize = 10,
                    Text = val.ToString("0.###"),
                    Width = left - 8,
                    TextAlignment = TextAlignment.Right
                };
                Canvas.SetLeft(lab, 0);
                Canvas.SetTop(lab, yy - 7);
                c.Children.Add(lab);
            }

            int n = angles.Count;
            if (tickStep < 1) tickStep = 30;

            for (int idx = 0; idx < n; idx += tickStep)
            {
                double frac = idx / (double)(n - 1);
                double xx = left + frac * plotW;

                Line tick = new Line { X1 = xx, Y1 = top + plotH, X2 = xx, Y2 = top + plotH + 4, Stroke = Brushes.Black, StrokeThickness = 1 };
                c.Children.Add(tick);

                TextBlock lab = new TextBlock { FontSize = 10, Text = angles[idx].ToString() };
                Canvas.SetLeft(lab, xx - 10);
                Canvas.SetTop(lab, top + plotH + 6);
                c.Children.Add(lab);
            }

            {
                int idx = n - 1;
                double xx = left + plotW;

                Line tick = new Line { X1 = xx, Y1 = top + plotH, X2 = xx, Y2 = top + plotH + 4, Stroke = Brushes.Black, StrokeThickness = 1 };
                c.Children.Add(tick);

                TextBlock lab = new TextBlock { FontSize = 10, Text = angles[idx].ToString() };
                Canvas.SetLeft(lab, xx - 10);
                Canvas.SetTop(lab, top + plotH + 6);
                c.Children.Add(lab);
            }

            TextBlock ylab = new TextBlock { FontSize = 10, Text = "MU/deg" };
            Canvas.SetLeft(ylab, left + 6);
            Canvas.SetTop(ylab, 0);
            c.Children.Add(ylab);

            TextBlock xlab = new TextBlock { FontSize = 10, Text = "Gantry angle (deg)" };
            Canvas.SetLeft(xlab, left + (plotW / 2.0) - 55);
            Canvas.SetTop(xlab, top + plotH + 24);
            c.Children.Add(xlab);

            if (avoidRanges != null && avoidRanges.Count > 0)
            {
                Brush faintRed = new SolidColorBrush(Color.FromArgb(170, 255, 0, 0));
                DoubleCollection smallDash = new DoubleCollection { 2, 3 };

                foreach (var ar in avoidRanges)
                {
                    double fracS = (angles.Count > 1) ? (ar.StartIdx / (double)(angles.Count - 1)) : 0.0;
                    double xS = left + fracS * plotW;

                    Line ls = new Line
                    {
                        X1 = xS,
                        Y1 = top,
                        X2 = xS,
                        Y2 = top + plotH,
                        Stroke = faintRed,
                        StrokeThickness = 1,
                        StrokeDashArray = smallDash,
                        Opacity = 0.7
                    };
                    c.Children.Add(ls);

                    TextBlock ts = new TextBlock
                    {
                        FontSize = 9,
                        Foreground = faintRed,
                        Text = ar.StartAngle.ToString(),
                        Opacity = 0.8
                    };
                    Canvas.SetLeft(ts, xS - 10);
                    Canvas.SetTop(ts, top + plotH + 16);
                    c.Children.Add(ts);

                    double fracE = (angles.Count > 1) ? (ar.EndIdx / (double)(angles.Count - 1)) : 0.0;
                    double xE = left + fracE * plotW;

                    Line le = new Line
                    {
                        X1 = xE,
                        Y1 = top,
                        X2 = xE,
                        Y2 = top + plotH,
                        Stroke = faintRed,
                        StrokeThickness = 1,
                        StrokeDashArray = smallDash,
                        Opacity = 0.55
                    };
                    c.Children.Add(le);

                    TextBlock te = new TextBlock
                    {
                        FontSize = 9,
                        Foreground = faintRed,
                        Text = ar.EndAngle.ToString(),
                        Opacity = 0.75
                    };
                    Canvas.SetLeft(te, xE - 10);
                    Canvas.SetTop(te, top + plotH + 16);
                    c.Children.Add(te);
                }
            }

            Polyline p = new Polyline { Stroke = Brushes.Green, StrokeThickness = 2 };
            for (int i = 0; i < n; i++)
            {
                double frac = i / (double)(n - 1);
                double xx = left + frac * plotW;
                double v = ySafe[i];
                double yy = top + plotH - ((v - ymin) / yr) * plotH;
                p.Points.Add(new Point(xx, yy));
            }
            c.Children.Add(p);

            return c;
        }

        private static Canvas BuildPolar_ZeroOnCircle_Sum(double[] mu, double width, double height, out PolarScale scale)
        {
            Canvas c = new Canvas();
            c.Width = width;
            c.Height = height;
            c.Background = Brushes.White;

            double cx = width * 0.42;
            double cy = height * 0.52;

            double minDim = Math.Min(width, height);
            double R0 = 0.18 * minDim;
            double Rmax = 0.42 * minDim;

            double[] muSafe = mu.Select(SafeMu).ToArray();

            double vmin = muSafe.Min();
            double vmax = muSafe.Max();
            double vmean = muSafe.Sum() / 360.0;

            if (vmax <= 1e-12) vmax = 1.0;
            double top = vmax;

            scale = new PolarScale { Min = vmin, Mean = vmean, Max = vmax, Top = top };

            double[] rings = new double[] { 0.0, 0.25 * top, 0.50 * top, 0.75 * top, 1.0 * top };

            Line h = new Line { X1 = cx - Rmax, Y1 = cy, X2 = cx + Rmax, Y2 = cy, Stroke = Brushes.LightGray, StrokeThickness = 1 };
            Line vline = new Line { X1 = cx, Y1 = cy - Rmax, X2 = cx, Y2 = cy + Rmax, Stroke = Brushes.LightGray, StrokeThickness = 1 };
            c.Children.Add(h);
            c.Children.Add(vline);

            for (int i = 0; i < rings.Length; i++)
            {
                double v = rings[i];
                double rr = R0 + (v / top) * (Rmax - R0);

                Ellipse ring = new Ellipse
                {
                    Width = 2 * rr,
                    Height = 2 * rr,
                    Stroke = (i == 0) ? Brushes.Gray : Brushes.LightGray,
                    StrokeThickness = (i == 0) ? 3 : 1
                };
                Canvas.SetLeft(ring, cx - rr);
                Canvas.SetTop(ring, cy - rr);
                c.Children.Add(ring);

                // 2 dp, and push the label a bit further right to reduce overlap
                TextBlock lab = new TextBlock { FontSize = 10, Foreground = Brushes.Gray, Text = v.ToString("0.00") };
                Canvas.SetLeft(lab, cx + rr + 14);
                Canvas.SetTop(lab, cy - 7);
                c.Children.Add(lab);
            }

            int[] degLabels = new int[] { 0, 90, 180, 270 };
            double rLabel = R0 - 14;
            if (rLabel < 10) rLabel = R0 * 0.5;

            for (int i = 0; i < degLabels.Length; i++)
            {
                int d = degLabels[i];
                double th = (90.0 - d) * Math.PI / 180.0;
                double x = cx + rLabel * Math.Cos(th);
                double y = cy - rLabel * Math.Sin(th);

                TextBlock lab = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, Text = d.ToString() };
                Canvas.SetLeft(lab, x - 10);
                Canvas.SetTop(lab, y - 8);
                c.Children.Add(lab);
            }

            Polyline shape = new Polyline { Stroke = Brushes.Green, StrokeThickness = 2 };
            for (int deg = 0; deg < 360; deg++)
            {
                double v = muSafe[deg];
                double rr = R0 + (v / top) * (Rmax - R0);
                if (rr < R0) rr = R0;
                if (rr > Rmax) rr = Rmax;

                double th = (90.0 - deg) * Math.PI / 180.0;
                double x = cx + rr * Math.Cos(th);
                double y = cy - rr * Math.Sin(th);

                shape.Points.Add(new Point(x, y));
            }
            if (shape.Points.Count > 0) shape.Points.Add(shape.Points[0]);
            c.Children.Add(shape);

            return c;
        }

        private class BeamRaw
        {
            public string BeamId;
            public GantryDirection Direction;
            public List<double> SSamples;
            public List<double> YSamples;
            public List<int> DisplayPathAngles;
        }

        private class BeamComputed
        {
            public string BeamId;
            public GantryDirection Direction;
            public List<int> Angles;
            public List<double> Values;

            public double Max;
            public double Min;
            public double Mean;
            public double Std;

            public List<AvoidRange> AvoidRanges;
        }

        private class PolarScale
        {
            public double Min;
            public double Mean;
            public double Max;
            public double Top;
        }
    }
}
