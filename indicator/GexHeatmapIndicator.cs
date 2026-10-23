using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ATAS.Indicators;
using Avalonia.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace GexLevels
{
    [DisplayName("GEX Heatmap")]
    [Description(
        "GEX Heatmap - real-time gamma exposure levels for BTC, ETH and SOL, " +
        "drawn as historical horizontal segments (DOM Levels style).\n\n" +
        "The indicator polls gexmonitor.com directly (public API, no login) " +
        "and records every change of the five key structural lines: FLIP " +
        "(gamma flip / HVL), call walls P1/P2 and put walls N1/N2.\n\n" +
        "Each level is drawn as a sequence of horizontal segments: while a " +
        "level keeps its value the segment runs on, and the moment the value " +
        "changes the old segment is frozen at its old price and a new segment " +
        "starts at the new price - so you can see where every line was at every " +
        "point in the past and exactly when it jumped.\n\n" +
        "Colors: FLIP orange dashed; P1/P2 red (P1 darker, P2 lighter); " +
        "N1/N2 green (N1 darker, N2 lighter).\n\n" +
        "Snapshots are saved locally per asset " +
        "(%AppData%\\ATAS X\\GexData\\gexheatmap_<asset>.json) with a 30-day " +
        "rolling window, so the full change history survives ATAS restarts.\n\n" +
        "Auto-detects the asset from the chart instrument: any symbol whose " +
        "name contains BTC, ETH or SOL (e.g. BTCUSDT, BTC-PERP) loads that " +
        "asset's data; other symbols draw nothing and make no network requests.")]
    public class GexHeatmapIndicator : Indicator
    {
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ATAS X", "GexData");

        private static readonly string[] LevelKeys = { "FLIP", "P1", "P2", "N1", "N2" };
        private static readonly string[] LevelLabels = { "FLIP", "P1", "P2", "N1", "N2" };

        // FLIP orange dashed; P1/P2 red dark/light; N1/N2 green dark/light.
        private static readonly Color[] DefaultColors =
        {
            Color.Orange,
            Color.FromArgb(220, 20, 20),
            Color.FromArgb(240, 100, 80),
            Color.FromArgb(20, 140, 60),
            Color.FromArgb(80, 220, 110),
        };
        private static readonly bool[] LevelDashed = { false, false, false, false, false };

        private static readonly Color LabelBg = Color.FromArgb(170, 5, 8, 12);
        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const int RetentionHours = 24 * 30;

        private sealed class Snapshot
        {
            public long T;              // unix seconds (UTC)
            public decimal FLIP, P1, P2, N1, N2;
        }

        private sealed class Seg
        {
            public int Bar1, Bar2;
            public decimal Price;
        }

        // ---- per-asset persistent state (kept for the chart's asset only) ----
        private string _asset = "";
        private readonly List<Snapshot> _snapshots = new();
        private Snapshot _lastSnapshot;   // most recent values for change detection
        private int _failCount;
        private DateTime _lastFetch = DateTime.MinValue;
        private bool _fetching;
        private static readonly HttpClient Http = new();

        // ---- rendering state ----
        private readonly List<Seg>[] _segments;
        private int _barCount = -1;
        private bool _segmentsDirty = true;
        private int _builtSig = -1;

        private bool _timerStarted;

        public GexHeatmapIndicator()
        {
            _segments = new List<Seg>[LevelKeys.Length];
            for (var i = 0; i < _segments.Length; i++)
                _segments[i] = new List<Seg>();

            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.LatestBar | DrawingLayouts.Final);
            Panel = IndicatorDataProvider.CandlesPanel;
            DenyToChangePanel = true;
            DrawAbovePrice = true;
            if (DataSeries.Count > 0)
                DataSeries[0].IsHidden = true;
        }

        #region Settings

        [Display(Name = "Poll interval (seconds)", GroupName = "Data", Order = 0)]
        public int PollSeconds
        {
            get => _pollSeconds;
            set => _pollSeconds = value < 5 ? 5 : (value > 120 ? 120 : value);
        }
        private int _pollSeconds = 10;

        [Display(Name = "Candle time zone", GroupName = "Data", Order = 1,
            Description = "Auto = detect; UTC / Local = force. Toggle if segments are shifted.")]
        public int TimeZoneMode
        {
            get => _tzMode;
            set => _tzMode = value < 0 ? 0 : (value > 2 ? 2 : value);
        }
        private int _tzMode = 0;

        [Display(Name = "History hours to draw", GroupName = "Display", Order = 2)]
        public int HistoryHours
        {
            get => _historyHours;
            set => _historyHours = value < 1 ? 1 : value;
        }
        private int _historyHours = 720;

        [Display(Name = "Line width", GroupName = "Display", Order = 3)]
        public int LineWidth
        {
            get => _lineWidth;
            set => _lineWidth = value < 1 ? 1 : (value > 8 ? 8 : value);
        }
        private int _lineWidth = 2;

        [Display(Name = "FLIP color", GroupName = "Colors", Order = 10)]
        public Color FlipColor { get; set; } = DefaultColors[0];
        [Display(Name = "P1 color", GroupName = "Colors", Order = 11)]
        public Color P1Color { get; set; } = DefaultColors[1];
        [Display(Name = "P2 color", GroupName = "Colors", Order = 12)]
        public Color P2Color { get; set; } = DefaultColors[2];
        [Display(Name = "N1 color", GroupName = "Colors", Order = 13)]
        public Color N1Color { get; set; } = DefaultColors[3];
        [Display(Name = "N2 color", GroupName = "Colors", Order = 14)]
        public Color N2Color { get; set; } = DefaultColors[4];

        private Color LevelColor(int i) => i switch
        {
            0 => FlipColor, 1 => P1Color, 2 => P2Color, 3 => N1Color, 4 => N2Color, _ => Color.Gray
        };

        [Display(Name = "Show labels", GroupName = "Labels", Order = 4)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Label font size", GroupName = "Labels", Order = 5)]
        public int LabelFontSize
        {
            get => _labelFontSize;
            set => _labelFontSize = value < 6 ? 6 : (value > 24 ? 24 : value);
        }
        private int _labelFontSize = 9;

        [Display(Name = "Show FLIP", GroupName = "Levels", Order = 20)]
        public bool ShowFlip { get; set; } = true;
        [Display(Name = "Show P1", GroupName = "Levels", Order = 21)]
        public bool ShowP1 { get; set; } = true;
        [Display(Name = "Show P2", GroupName = "Levels", Order = 22)]
        public bool ShowP2 { get; set; } = true;
        [Display(Name = "Show N1", GroupName = "Levels", Order = 23)]
        public bool ShowN1 { get; set; } = true;
        [Display(Name = "Show N2", GroupName = "Levels", Order = 24)]
        public bool ShowN2 { get; set; } = true;

        private bool LevelOn(int i) => i switch
        {
            0 => ShowFlip, 1 => ShowP1, 2 => ShowP2, 3 => ShowN1, 4 => ShowN2, _ => false
        };

        #endregion

        private int Sig()
        {
            var s = 0;
            for (var i = 0; i < LevelKeys.Length; i++) if (LevelOn(i)) s |= 1 << i;
            return (s * 397) ^ TimeZoneMode;
        }

        private string ResolveAsset() => AssetMatcher.Resolve(Instrument);

        private string DataFile(string asset) => Path.Combine(DataDir, $"gexheatmap_{asset}.json");

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar > _barCount) _barCount = bar;

            if (bar == 0 && !_timerStarted)
            {
                _timerStarted = true;
                var t = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(1, PollSeconds / 2)),
                    DispatcherPriority.Background, (_, _) => Tick());
                t.Start();
            }

            if (IsLoading) return;
            Tick();
        }

        private void Tick()
        {
            var asset = ResolveAsset();

            // Instrument changed or first run: load local history, reset.
            if (asset != _asset)
            {
                _asset = asset;
                _snapshots.Clear();
                _lastSnapshot = null;
                _failCount = 0;
                _lastFetch = DateTime.MinValue;
                if (!string.IsNullOrEmpty(asset))
                    LoadLocal(asset);
                _segmentsDirty = true;
                ForceRedraw();
            }

            if (string.IsNullOrEmpty(asset)) return;

            var due = (DateTime.UtcNow - _lastFetch).TotalSeconds >= PollSeconds;
            if (due && !_fetching)
                _ = FetchAsync(asset);
        }

        // ---- HTTP fetch on background thread --------------------------------
        private async Task FetchAsync(string asset)
        {
            _fetching = true;
            try
            {
                var url = $"https://gexmonitor.com/api/gex-latest?asset={asset}";
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var snap = Parse(body);
                if (snap == null)
                {
                    _failCount++;
                    return;
                }

                _failCount = 0;

                // Change-point compression: append only if any level moved;
                // otherwise bump the last snapshot's timestamp.
                lock (_snapshots)
                {
                    if (_lastSnapshot != null && SameValues(_lastSnapshot, snap))
                    {
                        _lastSnapshot.T = snap.T;
                        if (_snapshots.Count > 0) _snapshots[_snapshots.Count - 1] = _lastSnapshot;
                    }
                    else
                    {
                        _lastSnapshot = snap;
                        _snapshots.Add(snap);
                    }
                    Prune();
                    SaveLocal(asset);
                }

                _lastFetch = DateTime.UtcNow;
                _segmentsDirty = true;
                _ = Dispatcher.UIThread.InvokeAsync(ForceRedraw);
            }
            catch
            {
                _failCount++;
            }
            finally
            {
                _fetching = false;
            }
        }

        private static bool SameValues(Snapshot a, Snapshot b) =>
            a.FLIP == b.FLIP && a.P1 == b.P1 && a.P2 == b.P2 &&
            a.N1 == b.N1 && a.N2 == b.N2;

        private static Snapshot Parse(string body)
        {
            try
            {
                var root = JObject.Parse(body);
                var walls = root["profiles"]?["total"]?["walls"] as JObject;
                var meta = root["profiles"]?["total"]?["meta"] as JObject;
                if (walls == null) return null;

                decimal Get(string key, string field)
                {
                    var v = walls[key]?[field];
                    return v != null ? v.Value<decimal>() : 0m;
                }

                var flip = meta?["flip"] != null
                    ? meta["flip"].Value<decimal>()
                    : (root["flip_point"]?.Value<decimal>() ?? 0m);

                return new Snapshot
                {
                    T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    FLIP = flip,
                    P1 = Get("p1", "strike"),
                    P2 = Get("p2", "strike"),
                    N1 = Get("n1", "strike"),
                    N2 = Get("n2", "strike"),
                };
            }
            catch { return null; }
        }

        // ---- local storage (independent file, 30-day rolling) ---------------
        private void LoadLocal(string asset)
        {
            var path = DataFile(asset);
            if (!File.Exists(path)) return;
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var arr = root["snapshots"] as JArray;
                if (arr == null) return;
                foreach (var tok in arr)
                {
                    var s = new Snapshot
                    {
                        T = tok["t"]?.Value<long>() ?? 0,
                        FLIP = tok["FLIP"]?.Value<decimal>() ?? 0m,
                        P1 = tok["P1"]?.Value<decimal>() ?? 0m,
                        P2 = tok["P2"]?.Value<decimal>() ?? 0m,
                        N1 = tok["N1"]?.Value<decimal>() ?? 0m,
                        N2 = tok["N2"]?.Value<decimal>() ?? 0m,
                    };
                    if (s.T > 0) _snapshots.Add(s);
                }
                if (_snapshots.Count > 0)
                    _lastSnapshot = _snapshots[_snapshots.Count - 1];
                Prune();
            }
            catch { /* corrupt file: start fresh */ }
        }

        private void SaveLocal(string asset)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var path = DataFile(asset);
                var tmp = path + ".tmp";
                var arr = new JArray();
                foreach (var s in _snapshots)
                    arr.Add(new JObject
                    {
                        ["t"] = s.T,
                        ["FLIP"] = s.FLIP,
                        ["P1"] = s.P1,
                        ["P2"] = s.P2,
                        ["N1"] = s.N1,
                        ["N2"] = s.N2,
                    });
                var root = new JObject
                {
                    ["asset"] = asset,
                    ["retention_hours"] = RetentionHours,
                    ["updated_at"] = DateTime.UtcNow.ToString("o"),
                    ["snapshots"] = arr,
                };
                File.WriteAllText(tmp, root.ToString(Formatting.None));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { /* ignore write errors, data still in memory */ }
        }

        private void Prune()
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - RetentionHours * 3600;
            var keep = -1;
            for (var i = 0; i < _snapshots.Count; i++)
                if (_snapshots[i].T < cutoff) keep = i;
                else break;
            if (keep > 0) _snapshots.RemoveRange(0, keep);
        }

        private void ForceRedraw()
        {
            try { RedrawChart(new RedrawArg(Container.Region)); } catch { }
        }

        // ---- segment building ----------------------------------------------
        private bool DetectUtc()
        {
            if (TimeZoneMode == 1) return true;
            if (TimeZoneMode == 2) return false;
            try
            {
                var last = GetCandle(_barCount).Time;
                if (last.Kind == DateTimeKind.Utc) return true;
                if (last.Kind == DateTimeKind.Local) return false;

                var nowUtc = DateTime.UtcNow;
                DateTime probe = last;
                double age = Math.Abs((probe - nowUtc).TotalHours);
                for (var b = Math.Max(0, _barCount - 300); b <= _barCount; b++)
                {
                    var t = GetCandle(b).Time;
                    var a = Math.Abs((t - nowUtc).TotalHours);
                    if (a < age) { age = a; probe = t; }
                }
                if (age > 3.0) return true;
                var dU = Math.Abs((probe - nowUtc).TotalHours);
                var dL = Math.Abs((probe - nowUtc.ToLocalTime()).TotalHours);
                return dU <= dL;
            }
            catch { return true; }
        }

        private static DateTime ToChartTime(long epoch, bool utc)
        {
            var dto = DateTimeOffset.FromUnixTimeSeconds(epoch);
            return utc ? dto.UtcDateTime : dto.LocalDateTime;
        }

        private void RebuildSegments()
        {
            var sig = Sig();
            if (!_segmentsDirty && sig == _builtSig) return;
            _segmentsDirty = false;
            _builtSig = sig;

            for (var i = 0; i < LevelKeys.Length; i++) _segments[i].Clear();
            if (_barCount < 0) return;

            var utc = DetectUtc();
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(HistoryHours);

            for (var li = 0; li < LevelKeys.Length; li++)
            {
                if (!LevelOn(li)) continue;

                // Extract per-level change points from snapshots.
                var times = new List<DateTime>();
                var prices = new List<decimal>();
                for (var k = 0; k < _snapshots.Count; k++)
                {
                    var s = _snapshots[k];
                    if (UnixEpoch.AddSeconds(s.T) < cutoff)
                    {
                        // keep one anchor before the window
                        times.Clear(); prices.Clear();
                    }
                    times.Add(ToChartTime(s.T, utc));
                    prices.Add(LevelValue(s, li));
                }
                if (times.Count == 0) continue;

                var segs = _segments[li];
                int segStart = -1;
                decimal segPrice = 0m;

                var period = _barCount > 0
                    ? GetCandle(_barCount).Time - GetCandle(_barCount - 1).Time
                    : TimeSpan.FromMinutes(1);
                if (period <= TimeSpan.Zero) period = TimeSpan.FromMinutes(1);

                for (var b = 0; b <= _barCount; b++)
                {
                    var barEnd = b < _barCount ? GetCandle(b + 1).Time : GetCandle(b).Time + period;
                    var idx = times.BinarySearch(barEnd);
                    if (idx >= 0) idx--;      // boundary point belongs to next bar
                    else idx = ~idx - 1;

                    decimal? v;
                    if (idx >= 0) v = prices[idx];
                    else if (b == _barCount && prices.Count > 0) v = prices[prices.Count - 1];
                    else v = null;

                    if (v.HasValue)
                    {
                        if (segStart < 0) { segStart = b; segPrice = v.Value; }
                        else if (v.Value != segPrice)
                        {
                            segs.Add(new Seg { Bar1 = segStart, Bar2 = b - 1, Price = segPrice });
                            segStart = b; segPrice = v.Value;
                        }
                    }
                    else if (segStart >= 0)
                    {
                        segs.Add(new Seg { Bar1 = segStart, Bar2 = b - 1, Price = segPrice });
                        segStart = -1;
                    }
                }
                if (segStart >= 0)
                    segs.Add(new Seg { Bar1 = segStart, Bar2 = _barCount, Price = segPrice });
            }
        }

        private static decimal LevelValue(Snapshot s, int li) => li switch
        {
            0 => s.FLIP, 1 => s.P1, 2 => s.P2, 3 => s.N1, 4 => s.N2, _ => 0m
        };

        // ---- rendering -----------------------------------------------------
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            try
            {
                if (ChartInfo == null || _barCount < 0) return;
                var container = ChartInfo.PriceChartContainer;
                var region = Container.Region;
                if (container == null || region.Width <= 0) return;

                RebuildSegments();

                var font = new RenderFont("Segoe UI", LabelFontSize, FontStyle.Bold);
                var labelH = context.MeasureString("0123", font).Height + 3;
                var used = new List<Rectangle>();

                for (var li = 0; li < LevelKeys.Length; li++)
                {
                    if (!LevelOn(li)) continue;
                    var segs = _segments[li];
                    if (segs.Count == 0) continue;

                    var color = LevelColor(li);
                    var pen = new RenderPen(color, LineWidth,
                        LevelDashed[li] ? DashStyle.Dash : DashStyle.Solid);

                    for (var s = 0; s < segs.Count; s++)
                    {
                        var seg = segs[s];
                        int x1, x2;
                        try
                        {
                            x1 = (int)container.GetXByBar(seg.Bar1, true);
                            x2 = (int)container.GetXByBar(seg.Bar2, false);
                        }
                        catch { continue; }

                        if (seg.Bar2 == _barCount && x2 < region.Right)
                            x2 = region.Right;
                        if (x2 < region.Left || x1 > region.Right) continue;

                        var y = (int)container.GetYByPrice(seg.Price, false);
                        context.DrawLine(pen, x1, y, x2, y);

                        if (!ShowLabels) continue;

                        var isNew = s == segs.Count - 1 && seg.Bar2 == _barCount;
                        var text = isNew
                            ? LevelLabels[li] + " " + Fmt(seg.Price)
                            : Fmt(seg.Price);
                        var sz = context.MeasureString(text, font);

                        var vx1 = Math.Max(x1, region.Left);
                        var vx2 = Math.Min(x2, region.Right);
                        if (vx2 - vx1 < sz.Width + 4) continue;

                        var lx = (vx1 + vx2) / 2 - sz.Width / 2;
                        lx = Math.Max(region.Left + 2, Math.Min(lx, region.Right - sz.Width - 4));
                        var ly = y + 3;
                        if (ly + labelH > region.Bottom) ly = y - 3 - labelH;

                        var rect = new Rectangle(lx, ly, sz.Width + 4, labelH);
                        for (var k = 0; k < 4 && Overlaps(used, rect); k++)
                        {
                            rect = new Rectangle(rect.X, rect.Y + labelH + 1, rect.Width, rect.Height);
                            if (rect.Bottom > region.Bottom) break;
                        }
                        if (Overlaps(used, rect)) continue;
                        used.Add(rect);

                        var fg = isNew ? color : Color.FromArgb(160, color);
                        context.FillRectangle(LabelBg, rect);
                        context.DrawString(text, font, fg, rect.X + 2, rect.Y + 1);
                    }
                }

                // Warning after consecutive fetch failures.
                if (_failCount >= 5)
                {
                    var warn = $"GEX Heatmap: {_failCount} failed requests";
                    var wsz = context.MeasureString(warn, font);
                    var wr = new Rectangle(region.Left + 4, region.Top + 4,
                        wsz.Width + 6, labelH);
                    context.FillRectangle(Color.FromArgb(200, 120, 20, 20), wr);
                    context.DrawString(warn, font, Color.White, wr.X + 3, wr.Y + 1);
                }
            }
            catch { /* never crash the chart */ }
        }

        private static bool Overlaps(List<Rectangle> rs, Rectangle r)
        {
            foreach (var o in rs) if (o.IntersectsWith(r)) return true;
            return false;
        }

        private static string Fmt(decimal v) =>
            v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
