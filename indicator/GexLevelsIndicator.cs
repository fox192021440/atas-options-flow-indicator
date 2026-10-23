using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;

namespace GexLevels
{
    /// <summary>
    /// Maps a chart instrument to a gexmonitor asset. Any instrument whose
    /// name contains BTC / ETH / SOL (case-insensitive) loads that asset's
    /// data, e.g. BTCUSDT, BTC-PERP, BTCUSD, BTCUSDT_25DEC all use BTC.
    /// </summary>
    internal static class AssetMatcher
    {
        // Checked in order; the first contained keyword wins.
        private static readonly string[] Assets = { "BTC", "ETH", "SOL" };

        public static string Resolve(string instrument)
        {
            if (string.IsNullOrWhiteSpace(instrument))
                return "";
            var s = instrument.ToUpperInvariant();
            foreach (var asset in Assets)
                if (s.Contains(asset))
                    return asset;
            return "";
        }
    }

    /// <summary>One live snapshot from gexmonitor.com (levels + profile).</summary>
    internal sealed class GexLiveData
    {
        public decimal Spot, Flip, P1, P2, N1, N2, MP, EmHigh, EmLow;
        public readonly List<(decimal Strike, decimal Call, decimal Put, decimal Net)> Rows = new();
    }

    /// <summary>
    /// Shared HTTP client for the public gexmonitor.com API (no auth).
    /// One call to /api/gex-latest returns everything all indicators need:
    /// spot, flip, walls P1/P2/N1/N2, max pain, 1-day expected move and the
    /// full per-strike NET GEX profile.
    /// </summary>
    internal static class GexApi
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("GexAtasIndicator/1.0");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return h;
        }

        public static async Task<GexLiveData> FetchAsync(string asset)
        {
            var body = await Http
                .GetStringAsync($"https://gexmonitor.com/api/gex-latest?asset={asset}")
                .ConfigureAwait(false);
            return Parse(body);
        }

        public static GexLiveData Parse(string body)
        {
            var root = JObject.Parse(body);
            var total = root["profiles"]?["total"];
            var walls = total?["walls"];
            if (walls == null)
                return null;

            var meta = total["meta"];
            var d = new GexLiveData();

            decimal Wall(string key) => walls[key]?["strike"]?.Value<decimal>() ?? 0m;
            d.P1 = Wall("p1"); d.P2 = Wall("p2");
            d.N1 = Wall("n1"); d.N2 = Wall("n2");

            d.Flip = meta?["flip"]?.Value<decimal>()
                     ?? root["flip_point"]?.Value<decimal>() ?? 0m;
            d.MP = meta?["mp"]?.Value<decimal>() ?? 0m;
            d.EmHigh = meta?["expectedMove1d"]?["max"]?.Value<decimal>() ?? 0m;
            d.EmLow = meta?["expectedMove1d"]?["min"]?.Value<decimal>() ?? 0m;
            d.Spot = root["asset_price"]?.Value<decimal>()
                     ?? meta?["price"]?.Value<decimal>() ?? 0m;

            if (total["chartData"] is JArray rows)
            {
                foreach (var r in rows)
                {
                    var strike = r["strike"]?.Value<decimal?>() ?? null;
                    if (strike == null)
                        continue;
                    d.Rows.Add((strike.Value,
                        r["callGex"]?.Value<decimal>() ?? 0m,
                        r["putGex"]?.Value<decimal>() ?? 0m,
                        r["netGex"]?.Value<decimal>() ?? 0m));
                }
            }
            return d;
        }
    }

    [DisplayName("GEX Levels")]
    [Description(
        "GEX Levels - real-time gamma exposure levels for BTC, ETH and SOL.\n\n" +
        "This indicator draws the key structural price lines from the GEX Monitor " +
        "options analytics page as horizontal line segments on the price chart. " +
        "Each day is drawn as its own segment (segments are not connected across " +
        "UTC days), today's segment moves in real time as the data source updates, " +
        "and past segments stay frozen.\n\n" +
        "Data source: https://gexmonitor.com (public API, no login). The " +
        "indicator polls the API directly every few seconds and maintains a " +
        "local per-day archive (%AppData%\\ATAS X\\GexData\\gex_days_<asset>.json) " +
        "so past day segments survive restarts. No external poller or Python " +
        "is required.\n\n" +
        "Lines:\n" +
        "  FLIP   - Gamma flip point / HVL (sign of total gamma flips here).\n" +
        "  P1/P2  - Call walls (call resistance, nearest / further).\n" +
        "  N1/N2  - Put walls (put support, nearest / further).\n" +
        "  MP     - Max pain strike (hidden by default).\n" +
        "  EM HIGH / EM LOW - 1-day expected move band (hidden by default).\n" +
        "  SPOT   - Real-time spot price.\n\n" +
        "Parameters:\n" +
        "  Auto-detect asset from chart - when on, any instrument whose name " +
        "contains BTC, ETH or SOL (e.g. BTCUSDT, BTC-PERP, ETHUSD) automatically " +
        "shows that asset's levels; other instruments display nothing.\n" +
        "  Manual asset - BTC / ETH / SOL, used when auto-detect is off.\n" +
        "  Refresh interval (seconds) - how often the API is polled.\n" +
        "  History days to draw - number of recent day segments to display.\n" +
        "  Show FLIP / P1 / P2 / N1 / N2 / Max Pain / expected move / SPOT - toggle " +
        "individual lines on or off.")]
    public class GexLevelsIndicator : Indicator
    {
        // Shared data directory written by GexPoller: %AppData%\ATAS X\GexData
        private static readonly string DataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ATAS X", "GexData");

        private static readonly string[] LevelKeys =
            { "FLIP", "P1", "P2", "N1", "N2", "MP", "EM_HIGH", "EM_LOW", "SPOT" };

        private sealed class LevelDef
        {
            public readonly string Label;
            public readonly ValueDataSeries Series;

            public LevelDef(string label, ValueDataSeries series)
            {
                Label = label;
                Series = series;
            }
        }

        private sealed class Segment
        {
            public string Day;
            public int StartBar;
            public int EndBar;
        }

        private readonly List<LevelDef> _levels = new();
        private readonly List<Segment> _segments = new();

        private Dictionary<string, decimal[]> _days = new(StringComparer.Ordinal);

        private DateTime _lastFetch = DateTime.MinValue;
        private string _asset = "";
        private bool _fetching;
        private bool _timerStarted;
        private bool _wasLoading = true;
        private bool _segmentsDirty = true;
        private int _barCount = -1;
        private int _lastRebuiltCount = -1;
        private DateTime[] _dates = Array.Empty<DateTime>();

        public GexLevelsIndicator()
        {
            AddLevel("FLIP", "FLIP", System.Drawing.Color.Gold, 2, LineDashStyle.Solid);
            AddLevel("P1", "P1", System.Drawing.Color.LimeGreen, 1, LineDashStyle.Solid);
            AddLevel("P2", "P2", System.Drawing.Color.LimeGreen, 1, LineDashStyle.Dash);
            AddLevel("N1", "N1", System.Drawing.Color.OrangeRed, 1, LineDashStyle.Solid);
            AddLevel("N2", "N2", System.Drawing.Color.OrangeRed, 1, LineDashStyle.Dash);
            AddLevel("MP", "MP", System.Drawing.Color.Gray, 1, LineDashStyle.Dot);
            AddLevel("EM_HIGH", "EM HIGH", System.Drawing.Color.DodgerBlue, 1, LineDashStyle.Dash);
            AddLevel("EM_LOW", "EM LOW", System.Drawing.Color.DodgerBlue, 1, LineDashStyle.Dash);
            AddLevel("SPOT", "SPOT", System.Drawing.Color.White, 1, LineDashStyle.Dot);
        }

        private void AddLevel(string key, string label, System.Drawing.Color color, int width, LineDashStyle style)
        {
            var series = new ValueDataSeries(key, label)
            {
                Color = color,
                Width = width,
                LineDashStyle = style,
                ShowZeroValue = false,
                ShowCurrentValue = false,
                ScaleIt = false
            };
            DataSeries.Add(series);
            _levels.Add(new LevelDef(label, series));
        }

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

        private string ResolveAsset()
        {
            if (AutoDetect)
                return AssetMatcher.Resolve(Instrument);
            var a = (ManualAsset ?? "").Trim().ToUpperInvariant();
            return a is "BTC" or "ETH" or "SOL" ? a : "";
        }

        private static string DaysFile(string asset) =>
            System.IO.Path.Combine(DataDir, $"gex_days_{asset}.json");

        [Display(Name = "Refresh interval (seconds)", GroupName = "Data", Order = 2)]
        public int RefreshSeconds
        {
            get => _refreshSeconds;
            set => _refreshSeconds = value < 1 ? 1 : value;
        }
        private int _refreshSeconds = 5;

        [Display(Name = "History days to draw", GroupName = "Display", Order = 5)]
        public int MaxHistoryDays
        {
            get => _maxHistoryDays;
            set => _maxHistoryDays = value < 1 ? 1 : value;
        }
        private int _maxHistoryDays = 2;

        [Display(Name = "Show FLIP / HVL", GroupName = "Levels", Order = 10)]
        public bool ShowFlip { get; set; } = true;

        [Display(Name = "Show P1 (call resistance)", GroupName = "Levels", Order = 11)]
        public bool ShowP1 { get; set; } = true;

        [Display(Name = "Show P2", GroupName = "Levels", Order = 12)]
        public bool ShowP2 { get; set; } = true;

        [Display(Name = "Show N1 (put support)", GroupName = "Levels", Order = 13)]
        public bool ShowN1 { get; set; } = true;

        [Display(Name = "Show N2", GroupName = "Levels", Order = 14)]
        public bool ShowN2 { get; set; } = true;

        [Display(Name = "Show Max Pain", GroupName = "Levels", Order = 15)]
        public bool ShowMp { get; set; } = false;

        [Display(Name = "Show 1D expected move band", GroupName = "Levels", Order = 16)]
        public bool ShowEm { get; set; } = false;

        [Display(Name = "Show SPOT price", GroupName = "Levels", Order = 17)]
        public bool ShowSpot { get; set; } = true;

        #endregion

        private bool LevelEnabled(int index) => index switch
        {
            0 => ShowFlip,
            1 => ShowP1,
            2 => ShowP2,
            3 => ShowN1,
            4 => ShowN2,
            5 => ShowMp,
            6 => ShowEm,
            7 => ShowEm,
            8 => ShowSpot,
            _ => false
        };

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
            {
                _wasLoading = false;
                _segmentsDirty = true;
            }

            MaybeFetch();

            if (bar != _barCount)
                return;

            EnsureDates(bar + 1);
            var key = _dates[bar].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lastSegIsCurrent = _segments.Count > 0 && _segments[^1].Day == key;

            if (_segmentsDirty || bar > _lastRebuiltCount + 1 || (!lastSegIsCurrent && _days.ContainsKey(key)))
            {
                RebuildAll();
            }
            else
            {
                if (lastSegIsCurrent)
                    _segments[^1].EndBar = bar;
                WriteDayValues(bar, key);
            }
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

            // Asset switched or first run: drop the old days and load the new
            // asset's local archive so past segments appear immediately.
            if (asset != _asset)
            {
                _asset = asset;
                _days = new Dictionary<string, decimal[]>(StringComparer.Ordinal);
                if (asset.Length > 0)
                    LoadDaysHistory(asset);
                _segmentsDirty = true;
                if (!IsLoading && _barCount >= 0)
                {
                    RebuildAll();
                    TryRedraw();
                }
            }

            if (asset.Length == 0 || _fetching)
                return;

            _ = FetchAsync(asset);
        }

        private async Task FetchAsync(string asset)
        {
            _fetching = true;
            try
            {
                var d = await GexApi.FetchAsync(asset).ConfigureAwait(false);
                if (d == null)
                    return;

                var vals = new[]
                    { d.Flip, d.P1, d.P2, d.N1, d.N2, d.MP, d.EmHigh, d.EmLow, d.Spot };

                // Ignore responses with no usable levels at all.
                if (vals.All(v => v == 0m))
                    return;

                // The chart instrument may have switched while awaiting.
                if (asset != _asset)
                    return;

                _days[DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = vals;
                PruneDays();
                SaveDays(asset);
                _segmentsDirty = true;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsLoading || _barCount < 0)
                        return;
                    RebuildAll();
                    TryRedraw();
                });
            }
            catch
            {
                // Network/API errors: keep the previously drawn levels.
            }
            finally
            {
                _fetching = false;
            }
        }

        private void TryRedraw()
        {
            try { RedrawChart(new RedrawArg(Container.Region)); }
            catch { /* next chart refresh applies the levels */ }
        }

        // ---- local per-day archive (history cache, written by us) ---------
        private const int MaxArchivedDays = 30;

        private void LoadDaysHistory(string asset)
        {
            try
            {
                var path = DaysFile(asset);
                if (!File.Exists(path))
                    return;
                var map = ParseDaysFile(path);
                if (map != null && map.Count > 0)
                    _days = map;
            }
            catch { /* corrupt cache: live data will fill in */ }
        }

        private void PruneDays()
        {
            if (_days.Count <= MaxArchivedDays)
                return;
            foreach (var k in _days.Keys.OrderBy(k => k)
                         .Take(_days.Count - MaxArchivedDays).ToList())
                _days.Remove(k);
        }

        private void SaveDays(string asset)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var days = new JObject();
                foreach (var k in _days.Keys.OrderBy(k => k))
                {
                    var vals = _days[k];
                    var entry = new JObject();
                    for (var i = 0; i < LevelKeys.Length; i++)
                        if (vals[i] != 0m)
                            entry[LevelKeys[i]] = vals[i];
                    days[k] = entry;
                }
                var root = new JObject
                {
                    ["asset"] = asset,
                    ["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
                        CultureInfo.InvariantCulture),
                    ["today"] = DateTime.UtcNow.ToString("yyyy-MM-dd",
                        CultureInfo.InvariantCulture),
                    ["days"] = days,
                };
                var path = DaysFile(asset);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.None));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { /* archive write errors are non-fatal */ }
        }

        private static Dictionary<string, decimal[]> ParseDaysFile(string path)
        {
            var root = JObject.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, decimal[]>(StringComparer.Ordinal);

            if (root["days"] is JObject days)
            {
                foreach (var prop in days.Properties())
                {
                    if (prop.Value is not JObject day || string.IsNullOrEmpty(prop.Name))
                        continue;

                    var vals = new decimal[LevelKeys.Length];
                    var any = false;
                    for (var i = 0; i < LevelKeys.Length; i++)
                    {
                        var v = TryDecimal(day[LevelKeys[i]]);
                        if (v.HasValue && v.Value != 0m)
                        {
                            vals[i] = v.Value;
                            any = true;
                        }
                    }

                    if (any)
                        map[prop.Name] = vals;
                }
            }

            if (map.Count == 0 && root["levels"] is JObject levels)
            {
                // Fallback: legacy/current-day snapshot (gex_levels.json format).
                var vals = new decimal[LevelKeys.Length];
                var any = false;
                for (var i = 0; i < LevelKeys.Length; i++)
                {
                    var v = TryDecimal(levels[LevelKeys[i]]?["price"]);
                    if (v.HasValue && v.Value != 0m)
                    {
                        vals[i] = v.Value;
                        any = true;
                    }
                }

                if (any)
                {
                    var updated = root.Value<string>("updated_at");
                    var key = !string.IsNullOrEmpty(updated) && updated.Length >= 10
                        ? updated.Substring(0, 10)
                        : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    map[key] = vals;
                }
            }

            return map;
        }

        private static decimal? TryDecimal(JToken token)
        {
            if (token == null)
                return null;
            try
            {
                return token.Value<decimal>();
            }
            catch
            {
                return null;
            }
        }

        private void EnsureDates(int count)
        {
            if (_dates.Length >= count)
                return;

            var old = _dates;
            _dates = new DateTime[count];
            var keep = Math.Min(old.Length, count);
            if (keep > 0)
                Array.Copy(old, _dates, keep);

            for (var b = old.Length; b < count; b++)
                _dates[b] = GetCandle(b).Time.Date;
        }

        private void WriteDayValues(int bar, string key)
        {
            _days.TryGetValue(key, out var vals);
            for (var i = 0; i < _levels.Count; i++)
                _levels[i].Series[bar] = vals != null && LevelEnabled(i) ? vals[i] : 0m;
        }

        private void RebuildAll()
        {
            _segmentsDirty = false;
            _segments.Clear();

            foreach (var lv in _levels)
                lv.Series.Clear();

            if (_barCount < 0)
                return;

            EnsureDates(_barCount + 1);

            if (_days.Count == 0)
            {
                _lastRebuiltCount = _barCount;
                return;
            }

            var drawDays = new HashSet<string>(
                _days.Keys.OrderByDescending(k => k).Take(MaxHistoryDays),
                StringComparer.Ordinal);

            var start = 0;
            while (start <= _barCount)
            {
                var day = _dates[start];
                var end = start;
                while (end + 1 <= _barCount && _dates[end + 1] == day)
                    end++;

                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (drawDays.Contains(key) && _days.TryGetValue(key, out var vals))
                {
                    var any = false;
                    for (var b = start; b <= end; b++)
                    {
                        for (var i = 0; i < _levels.Count; i++)
                        {
                            var v = LevelEnabled(i) ? vals[i] : 0m;
                            _levels[i].Series[b] = v;
                            any |= v != 0m;
                        }
                    }

                    if (any)
                        _segments.Add(new Segment { Day = key, StartBar = start, EndBar = end });

                    // 用透明色遮住前一天最后一天连到当天第一天的"竖线/斜线"
                    for (var i = 0; i < _levels.Count; i++)
                        _levels[i].Series.Colors[start] = System.Drawing.Color.Transparent;
                }

                start = end + 1;
            }

            _lastRebuiltCount = _barCount;
        }
    }
}
