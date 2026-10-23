# ATAS Options Flow Indicator

A suite of custom indicators for **ATAS X** that renders options-market gamma exposure (GEX) directly on the chart. Levels, walls and the full per-strike gamma profile are pulled from the public **gexmonitor.com** API and drawn natively by the platform's rendering engine.

No external scripts, no local polling daemons, no manual JSON files — the indicators fetch everything themselves.

---

## Indicators

The solution builds a single assembly (`GexLevels.dll`) that exposes three separate indicators inside ATAS X.

### 1. GEX Levels

Draws the option-derived price levels as horizontal lines across the session, one set per trading day.

| Level | Meaning |
| --- | --- |
| **FLIP / HVL** | Gamma flip level — the price where net dealer gamma changes sign. Above it dealers are typically long gamma (price gets pinned), below it they are short gamma (moves get amplified). |
| **P1 / P2** | Primary and secondary call-side gamma walls — strike clusters that act as resistance. |
| **N1 / N2** | Primary and secondary put-side gamma walls — strike clusters that act as support. |
| **Max Pain** | The strike at which the largest number of option contracts expire worthless. |
| **1D Expected Move** | The one-day implied range, drawn as a band around spot. |
| **SPOT** | Reference line for the current underlying price. |

Historical levels are archived locally per day, so the lines survive restarts and remain visible on past sessions.

### 2. GEX Volume Profile

A horizontal histogram of the **net GEX profile** — one bar per option strike, aligned to the price scale.

- Bar length is proportional to the strike's net gamma (call GEX minus put GEX).
- All bars extend leftward from an anchor line at the right edge of the strip, so the profile stays out of the way of price action.
- Sign is conveyed by colour: positive net gamma one colour, negative the other.

### 3. GEX Heatmap

A time-based heatmap in the style of ATAS's built-in **DOM Levels**: each level is drawn as a horizontal segment that spans only the period during which it was active, leaving a visual history of how the walls migrated over time.

Useful for seeing whether a wall is holding, drifting, or being consumed.

---

## Supported assets

`BTC`, `ETH`, `SOL`.

**Auto-detection** — any instrument whose name contains `BTC`, `ETH` or `SOL` (case-insensitive) is mapped to that asset automatically. `BTCUSDT`, `BTC-PERP`, `BTCUSD`, `BTCUSDT_25DEC` all resolve to BTC.

**Manual override** — turn auto-detection off and pick the asset explicitly. Useful for CFDs, spot pairs or any instrument whose name does not contain the asset ticker.

---

## Requirements

- **ATAS X** on Windows
- **.NET 10 SDK** (the project targets `net10.0-windows`)
- Reference assemblies from your ATAS X installation

---

## Build from source

```bash
cd indicator
dotnet build -c Release
```

The compiled `GexLevels.dll` is written to `indicator/bin/Release/`.

> **Note on reference paths.** `GexLevels.csproj` resolves the ATAS assemblies through hard-coded `HintPath` entries pointing at `d:\application\ATAS X\`. If ATAS X is installed elsewhere, update these four references before building:
>
> - `ATAS.Indicators.dll`
> - `Avalonia.Base.dll`
> - `OFT.Rendering.dll`
> - `Newtonsoft.Json.dll`

---

## Installation

1. Download `Option.Flow.Indicator.zip` from the [Releases](../../releases) page.
2. Extract `GexLevels.dll` into `%APPDATA%\ATAS X\Indicators\`.
3. Restart ATAS X.
4. Add **GEX Levels**, **GEX Volume Profile** or **GEX Heatmap** from the indicator list.

If you build from source instead, the same folder is the destination.

---

## Settings

### GEX Levels

| Setting | Default | Group |
| --- | --- | --- |
| Auto-detect asset from chart | `true` | Data |
| Manual asset (BTC/ETH/SOL) | — | Data |
| Refresh interval (seconds) | `5` | Data |
| History days to draw | `2` | Display |
| Show FLIP / HVL | `true` | Levels |
| Show P1 (call resistance) | `true` | Levels |
| Show P2 | `true` | Levels |
| Show N1 (put support) | `true` | Levels |
| Show N2 | `true` | Levels |
| Show Max Pain | `false` | Levels |
| Show 1D expected move band | `false` | Levels |
| Show SPOT price | `true` | Levels |

### GEX Volume Profile

| Setting | Default | Group |
| --- | --- | --- |
| Auto-detect asset from chart | `true` | Data |
| Manual asset (BTC/ETH/SOL) | — | Data |
| Refresh interval (seconds) | `5` | Data |
| Strip width | `230` | Layout |
| Price axis reserve | `0` | Layout |
| Bar thickness (px) | `6` | Layout |
| Bar opacity | `190` | Layout |
| Strip background colour | `#090C12` @ 225 alpha | Colors |
| Positive net GEX colour | LimeGreen | Colors |
| Negative net GEX colour | OrangeRed | Colors |
| Show value labels | `true` | Overlays |
| Show spot marker | `true` | Overlays |
| Show title | `true` | Overlays |

### GEX Heatmap

| Setting | Default | Group |
| --- | --- | --- |
| Poll interval (seconds) | `10` | Data |
| Candle time zone | UTC | Data |
| History hours to draw | `720` | Display |
| Line width | `2` | Display |
| Show labels | `true` | Labels |
| Label font size | `9` | Labels |
| Show FLIP / P1 / P2 / N1 / N2 | `true` | Levels |
| Per-level colours | — | Colors |

---

## Data source

All three indicators poll the same public endpoint:

```
GET https://gexmonitor.com/api/gex-latest?asset=BTC
```

No API key, no login, no external poller.

Response shape (only the fields the indicators consume are shown):

```jsonc
{
  "asset_price": 81630.3,          // spot
  "flip_point": 81200.0,           // fallback if meta.flip is absent
  "profiles": {
    "total": {
      "walls": {
        "p1": { "strike": 83000 },  // call wall, nearest
        "p2": { "strike": 85000 },  // call wall, further
        "n1": { "strike": 80000 },  // put wall, nearest
        "n2": { "strike": 78000 }   // put wall, further
      },
      "meta": {
        "flip": 81200.0,
        "mp": 81000.0,                       // max pain
        "price": 81630.3,
        "expectedMove1d": { "min": 80200, "max": 83100 }
      },
      "chartData": [                         // per-strike profile
        { "strike": 80000, "callGex": 0, "putGex": 123456, "netGex": -123456 }
      ]
    }
  }
}
```

A single shared `GexApi` class performs the request, so one HTTP call per refresh supplies every field the indicators need.

**Local archive.** GEX Levels keeps a per-day snapshot at `%AppData%\ATAS X\GexData\gex_days_<asset>.json` so historical segments survive restarts. Delete that file to force a clean re-fetch.

---

## Project layout

```
indicator/
├── GexLevels.csproj          project file (assembly: GexLevels.dll)
├── GexLevelsIndicator.cs     GEX Levels
├── GexVpIndicator.cs         GEX Volume Profile
└── GexHeatmapIndicator.cs    GEX Heatmap
```

---

## Version history

### 26.10.0.2 — 2026-10-18

**Fixed the data-connection failure.** GEX Levels and GEX Volume Profile previously relied on a local `gex_poller.py` script that had to run continuously to write JSON files; once the script stopped — or on a machine where it was never installed — the data froze.

Both indicators now poll the public gexmonitor.com API directly, the same way GEX Heatmap already did:

- `GexLevelsIndicator.cs` fetches FLIP / wall levels / spot / Max Pain / expected move every refresh cycle and keeps its own local per-day archive (survives restarts).
- `GexVpIndicator.cs` fetches the per-strike net GEX profile directly.
- A shared `GexApi` class serves both indicators, so a single request provides every field they need.
- The old JSON-file-path parameter is replaced by **Manual asset** (BTC / ETH / SOL) when auto-detection is off.
- Auto-detection rule unchanged: instrument names containing `BTC`, `ETH` or `SOL` map to that asset.

No external Python script is required any more.

### 26.10.0.1 — 2026-10-06

- Added **GEX Levels Plus**, shipped in the same `GexLevels.dll` alongside GEX Levels and GEX Volume Profile.
- GEX Levels / Volume Profile updated to the 2026-10-05 → 2026-10-06 revision.

---

## Disclaimer

This project is an independent, unofficial tool. It is not affiliated with, endorsed by, or supported by ATAS or gexmonitor.com.

Gamma exposure data is provided by a third-party public endpoint and may be delayed, incomplete, or unavailable. The indicators are visualisation tools only — they do not generate trading signals and nothing here constitutes financial advice. Use at your own risk.

---

## License

No license file is currently included. All rights reserved by the author unless stated otherwise.
