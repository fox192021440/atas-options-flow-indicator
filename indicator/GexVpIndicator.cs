using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ATAS.Indicators;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace GexLevels
{
    [DisplayName("GEX Volume Profile")]
    [Description(
        "GEX Volume Profile - a horizontal histogram of the NET GEX profile: one " +
        "bar per option strike, aligned with the price scale.\n\n" +
        "At each strike the bar length is proportional to the strike's NET gamma " +
        "(call GEX minus put GEX). ALL bars extend LEFTWARD from an anchor line on " +
        "the right edge of the strip, regardless of sign; the sign is shown by " +
        "color: positive net = Call GEX color, negative net = Put GEX color. Bar " +
        "lengths auto-scale to the strikes visible in the current price window, and " +
        "the concrete net value is printed at each bar. The strip is flush with " +
        "the right edge of the chart, its background color is configurable, and " +
        "the bars are nearly as thick as the price candles. Data refreshes in " +
        "near real time: the indicator polls the public gexmonitor.com API " +
        "directly every few seconds - no external poller or Python required.\n\n" +
        "Auto-detect asset from chart: any instrument whose name contains BTC, " +
        "ETH or SOL (e.g. BTCUSDT, BTC-PERP, ETHUSD) shows that asset's " +
        "profile; any other instrument shows nothing.\n\n" +
        "Parameters:\n" +
        "  Manual asset - BTC / ETH / SOL, used when auto-detect is off.\n" +
        "  Refresh interval (seconds) - how often the API is polled.\n" +
        "  Strip width - width of the profile strip on the right.\n" +
        "  Bar thickness % - bar height as % of the gap between adjacent strikes.\n" +
        "  Price axis reserve - space reserved for the right-side price labels.\n" +
        "  Bar opacity - transparency of the bars (0-255).\n" +
        "  Positive / Negative net GEX color - bar colors for the two signs.\n" +
        "  Strip background color - color of the strip background.\n" +
        "  Show value labels / spot marker / title - toggle helpers.\n\n" +
        "Data source: https://gexmonitor.com (public API, no login required).")]
    public class GexVpIndicator : Indicator
    {
        // Shared data directory written by GexPoller: %AppData%\ATAS X\GexData
        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ATAS X", "GexData");

        private sealed class VpRow
        {
            public decimal Strike;
            public decimal Call;
            public decimal Put;
            public decimal Net;
        }

        private List<VpRow> _rows = new();
        private decimal _spot;
        private string _updatedAt = "";

        private DateTime _lastFetch = DateTime.MinValue;
        private string _asset = "";
        private bool _fetching;
        private bool _timerStarted;
        private bool _wasLoading = true;
        private int _barCount = -1;

        #region Settings

        [Display(Name = "Auto-detect asset from chart", GroupName = "Data", Order = 0)]
        public bool AutoDetect
        {
            get => _autoDetect;
            set
            {
                _autoDetect = value;
                _lastFetch = DateTime.MinValue; // force re-resolve + reload
            }
        }
        private bool _autoDetect = true;

        [Display(Name = "Manual asset (BTC/ETH/SOL)", GroupName = "Data", Order = 1,
            Description = "Force an asset when auto-detect is off.")]
        public string ManualAsset
        {
            get => _manualAsset;
            set
            {
                _manualAsset = value;
                _lastFetch = DateTime.MinValue; // force re-resolve + reload
            }
        }
        private string _manualAsset = "";

        [Display(Name = "Refresh interval (seconds)", GroupName = "Data", Order = 2)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set => _refreshSeconds = value < 1 ? 1 : value;
        }
        private int _refreshSeconds = 5;

        [Display(Name = "Strip width", GroupName = "Layout", Order = 5)]
        public int StripWidth
        {
            get => _stripWidth;
            set => _stripWidth = value < 80 ? 80 : value;
        }
        private int _stripWidth = 230;

        [Display(Name = "Price axis reserve", GroupName = "Layout", Order = 7)]
        public int PriceAxisReserve
        {
            get => _priceAxisReserve;
            set => _priceAxisReserve = value < 0 ? 0 : value;
        }
        private int _priceAxisReserve = 0;

        [Display(Name = "Bar thickness (px)",
            Description = "Fixed bar height in pixels (1-30). Bars keep this exact " +
            "pixel height regardless of chart zoom or scroll.",
            GroupName = "Layout", Order = 6)]
        public int BarThickness
        {
            get => _barThickness;
            set => _barThickness = value < 1 ? 1 : (value > 30 ? 30 : value);
        }
        private int _barThickness = 6;

        [Display(Name = "Bar opacity", GroupName = "Layout", Order = 8)]
        public int Opacity
        {
            get => _opacity;
            set => _opacity = value < 0 ? 0 : (value > 255 ? 255 : value);
        }
        private int _opacity = 190;

        [Display(Name = "Strip background color", GroupName = "Colors", Order = 8)]
        public Color BackgroundColor { get; set; } = Color.FromArgb(225, 9, 12, 18);

        [Display(Name = "Positive net GEX color", GroupName = "Colors", Order = 10)]
        public Color CallColor { get; set; } = Color.LimeGreen;

        [Display(Name = "Negative net GEX color", GroupName = "Colors", Order = 11)]
        public Color PutColor { get; set; } = Color.OrangeRed;

        [Display(Name = "Show value labels", GroupName = "Overlays", Order = 20)]
        public bool ShowValues { get; set; } = true;

        [Display(Name = "Show spot marker", GroupName = "Overlays", Order = 21)]
        public bool ShowSpotMarker { get; set; } = true;

        [Display(Name = "Show title", GroupName = "Overlays", Order = 22)]
        public bool ShowTitle { get; set; } = true;

        #endregion

        public GexVpIndicator()
        {
            // Required for OnRender to be called at all (ATAS docs: Drawing basics).
            EnableCustomDrawing = true;
            // LatestBar = redraw on every tick; Final = redraw on every chart repaint.
            SubscribeToDrawingEvents(DrawingLayouts.LatestBar | DrawingLayouts.Final);

            // Pin the indicator to the main candles (price) panel.
            Panel = IndicatorDataProvider.CandlesPanel;
            DenyToChangePanel = true;
            DrawAbovePrice = true;

            // Hide the default (unused) data series so it draws nothing itself.
            if (DataSeries.Count > 0)
                DataSeries[0].IsHidden = true;
        }

        private string ResolveAsset()
        {
            if (AutoDetect)
                return AssetMatcher.Resolve(Instrument);
            var a = (ManualAsset ?? "").Trim().ToUpperInvariant();
            return a is "BTC" or "ETH" or "SOL" ? a : "";
        }

        private static string VpFile(string asset) =>
            Path.Combine(DataDir, $"gex_vp_{asset}.json");

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0 && !_timerStarted)
            {
                _timerStarted = true;
                StartTimer();
            }

            if (bar > _barCount)
                _barCount = bar;

            if (IsLoading)
            {
                _wasLoading = true;
                return;
            }

            if (_wasLoading)
                _wasLoading = false;

            MaybeFetch();
        }

        private void StartTimer()
        {
            try
            {
                var timer = new DispatcherTimer(TimeSpan.FromSeconds(RefreshSeconds),
                    DispatcherPriority.Background, (s, e) => MaybeFetch());
                timer.Start();
            }
            catch
            {
                // Timer is only a convenience; OnCalculate also fetches on ticks.
            }
        }

        // ---- live data (HTTP) ---------------------------------------------
        private void MaybeFetch()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFetch).TotalSeconds < RefreshSeconds || _fetching)
                return;
            _lastFetch = now;

            var asset = ResolveAsset();

            // Asset switched or first run: clear and show the local cache (if
            // any) until the first HTTP reply arrives.
            if (asset != _asset)
            {
                _asset = asset;
                _rows = new List<VpRow>();
                _spot = 0m;
                _updatedAt = "";
                if (asset.Length > 0)
                    LoadLocalOnce(asset);
                ForceRepaint();
            }

            if (asset.Length == 0 || _fetching)
                return;

            _ = FetchAsync(asset);
        }

        private void LoadLocalOnce(string asset)
        {
            try
            {
                var path = VpFile(asset);
                if (File.Exists(path))
                    ParseFile(path);
            }
            catch { /* corrupt cache: live data will fill in */ }
        }

        private async Task FetchAsync(string asset)
        {
            _fetching = true;
            try
            {
                var d = await GexApi.FetchAsync(asset).ConfigureAwait(false);
                if (d == null || d.Rows.Count == 0)
                    return;

                // The chart instrument may have switched while awaiting.
                if (asset != _asset)
                    return;

                _rows = d.Rows.Select(r => new VpRow
                    { Strike = r.Strike, Call = r.Call, Put = r.Put, Net = r.Net })
                    .ToList();
                _spot = d.Spot;
                _updatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                    CultureInfo.InvariantCulture);

                await Dispatcher.UIThread.InvokeAsync(ForceRepaint);
            }
            catch
            {
                // Network/API errors: keep the previously drawn profile.
            }
            finally
            {
                _fetching = false;
            }
        }

        private void ParseFile(string path)
        {
            var root = JObject.Parse(File.ReadAllText(path));
            var rows = new List<VpRow>();

            if (root["rows"] is JArray arr)
            {
                foreach (var token in arr)
                {
                    if (token is not JArray a || a.Count < 4) continue;
                    rows.Add(new VpRow
                    {
                        Strike = a[0].Value<decimal>(),
                        Call = a[1].Value<decimal>(),
                        Put = a[2].Value<decimal>(),
                        Net = a[3].Value<decimal>()
                    });
                }
            }

            _rows = rows;
            _spot = root["spot"]?.Value<decimal?>() ?? 0m;
            _updatedAt = root.Value<string>("updated_at") ?? "";
        }

        private void ForceRepaint()
        {
            try { RedrawChart(new RedrawArg(Container.Region)); } catch { }
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            try
            {
                if (_rows.Count == 0 || ChartInfo == null)
                    return;

                var container = ChartInfo.PriceChartContainer;
                var region = Container.Region;
                if (container == null || region.Width <= 0 || region.Height <= 0)
                    return;

                var stripRight = region.Right - PriceAxisReserve;
                var stripX = stripRight - StripWidth;
                var strip = new Rectangle(stripX, region.Top, StripWidth, region.Height);

                var titleH = ShowTitle ? 18 : 4;
                var contentTop = region.Top + titleH;

                context.SetClip(strip);

                // Background (user-configurable)
                context.FillRectangle(BackgroundColor, strip);

                // Bars all grow LEFT from this anchor flush with the right edge.
                var anchorX = stripRight - 1;
                var maxLen = StripWidth - 6;

                // Title + color legend
                var fontTitle = new RenderFont("Segoe UI", 9, FontStyle.Bold);
                var fontSmall = new RenderFont("Segoe UI", 8);
                if (ShowTitle)
                {
                    context.DrawString("NET GEX PROFILE", fontTitle, Color.White,
                        stripX + 4, region.Top + 3);
                    if (!string.IsNullOrEmpty(_updatedAt))
                    {
                        var t = _updatedAt.Length >= 19
                            ? _updatedAt.Substring(11, 8) + " UTC"
                            : "";
                        var ts = context.MeasureString(t, fontSmall);
                        context.DrawString(t, fontSmall, Color.Gray,
                            anchorX - ts.Width - 4, region.Top + 4);
                    }
                    // Color legend (direction is always leftward; sign by color).
                    context.DrawString("NET+", fontSmall,
                        Color.FromArgb(200, CallColor), stripX + 100, region.Top + 4);
                    context.DrawString("NET-", fontSmall,
                        Color.FromArgb(200, PutColor), stripX + 134, region.Top + 4);
                }

                // Scale against the max net GEX across the WHOLE dataset (not
                // just visible strikes), so bar lengths stay fixed while the
                // chart is scrolled or zoomed. Lengths only change when the
                // underlying data file updates.
                decimal maxMag = 0m;
                foreach (var r in _rows)
                {
                    var m = Math.Abs(r.Net);
                    if (m > maxMag) maxMag = m;
                }
                if (maxMag <= 0m)
                {
                    context.ResetClip();
                    return;
                }

                // Build the visible, strike-sorted (high->low) item list.
                var visible = _rows
                    .Select(r => new
                    {
                        Row = r,
                        Y = (int)container.GetYByPrice(r.Strike, false)
                    })
                    .Where(v => v.Y >= contentTop && v.Y <= region.Bottom)
                    .OrderByDescending(v => v.Row.Strike)
                    .ToList();

                // Fixed pixel bar height, independent of chart zoom/scroll.
                var callFill = Color.FromArgb(Opacity, CallColor);
                var putFill = Color.FromArgb(Opacity, PutColor);

                for (var i = 0; i < visible.Count; i++)
                {
                    var v = visible[i];
                    var r = v.Row;
                    var y = v.Y;

                    var barH = BarThickness;
                    var barY = y - barH / 2;

                    // Every bar grows LEFT from the anchor regardless of sign.
                    var len = (int)(Math.Abs(r.Net) / maxMag * maxLen);
                    if (len <= 0)
                        continue;

                    var tipX = anchorX - len;
                    context.FillRectangle(r.Net >= 0m ? callFill : putFill,
                        new Rectangle(tipX, barY, len, barH));

                    if (ShowValues && len >= 24 && barH >= 9)
                    {
                        var txt = Compact(r.Net);
                        var signColor = r.Net >= 0m ? CallColor : PutColor;
                        var sz = context.MeasureString(txt, fontSmall);

                        if (tipX - 3 - sz.Width >= stripX + 2)
                        {
                            // Label just outside the left tip, vertically centered.
                            context.DrawString(txt, fontSmall, signColor,
                                tipX - 3 - sz.Width, y - sz.Height / 2);
                        }
                        else if (barH >= 11)
                        {
                            // Not enough room: draw inside the bar near its tip.
                            context.DrawString(txt, fontSmall, Color.White,
                                tipX + 3, y - sz.Height / 2);
                        }
                    }
                }

                // Anchor line (all bars start here)
                context.DrawLine(new RenderPen(Color.FromArgb(170, Color.White), 1f),
                    anchorX, contentTop, anchorX, region.Bottom);

                // Spot marker
                if (ShowSpotMarker && _spot > 0m)
                {
                    var ySpot = container.GetYByPrice(_spot, false);
                    if (ySpot >= contentTop && ySpot <= region.Bottom)
                    {
                        var pen = new RenderPen(Color.White, 1f, DashStyle.Dash);
                        context.DrawLine(pen, stripX, ySpot, stripRight, ySpot);
                        context.DrawString("SPOT", fontSmall, Color.White,
                            stripX + 3, ySpot + 1);
                    }
                }

                // Border
                context.DrawRectangle(new RenderPen(Color.FromArgb(120, Color.Gray), 1f), strip);

                context.ResetClip();
            }
            catch
            {
                // Never let drawing errors affect the platform.
                try { context.ResetClip(); } catch { }
            }
        }

        private static string Compact(decimal v)
        {
            var a = Math.Abs(v);
            if (a >= 1_000_000_000m) return (v / 1_000_000_000m).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (a >= 1_000_000m) return (v / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (a >= 1_000m) return (v / 1_000m).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
