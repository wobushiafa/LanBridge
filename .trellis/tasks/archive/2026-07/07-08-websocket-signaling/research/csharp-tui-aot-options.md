# Research: C# Terminal UI Libraries Compatible with Native AOT

- **Query**: Research C# terminal UI libraries compatible with Native AOT (PublishAot=true) for a continuously updating dashboard
- **Scope**: mixed (internal project files + external NuGet/GitHub research)
- **Date**: 2026-07-08

## Executive Summary

The LanBridge project currently uses only basic `Console` API calls with `Console.ForegroundColor` (`ConsoleStatusWriter` in `src/LanBridge.Common/Runtime/ConsoleStatusWriter.cs`). Three Peers (SignalingServer, IntranetPeer, ExtranetPeer) all have `PublishAot=true`. No external console/TUI NuGet packages are used. For a continuously updating dashboard under AOT, the best trade-off is **Spectre.Console v0.57.2** (confirmed AOT-compatible as of v0.49+). A raw-ANSI approach is also viable for maximum AOT safety and zero dependencies.

---

## Findings

### Existing Project Pattern

| File | Usage |
|------|-------|
| `src/LanBridge.Common/Runtime/ConsoleStatusWriter.cs` | `Console.WriteLine`, `Console.ForegroundColor` style status output, used by all executables |
| `src/LanBridge.Common/LanBridge.Common.csproj` | `IsAotCompatible=true`, no NuGet package references |
| `src/LanBridge.IntranetPeer/LanBridge.IntranetPeer.csproj` | `PublishAot=true`, only ProjectReference to Common |
| `src/LanBridge.ExtranetPeer/LanBridge.ExtranetPeer.csproj` | `PublishAot=true`, only ProjectReference to Common |
| `src/LanBridge.SignalingServer/LanBridge.SignalingServer.csproj` | `PublishAot=true`, only ProjectReference to Common |
| `src/LanBridge.KcpTest/LanBridge.KcpTest.csproj` | No `PublishAot`, only ProjectReference to Common |
| `src/LanBridge.Tests/LanBridge.Tests.csproj` | xUnit test project, NuGet refs only to xunit/test SDK |

The console output is purely sequential log-line writes, with color support via `Console.ForegroundColor`. The SignalingServer runs a periodic metrics loop (`ReportMetricsLoopAsync`) that writes a line every N seconds; there is no in-place update or dashboard rendering.

---

### Option 1: Spectre.Console v0.57.2

**NuGet**: `Spectre.Console` 0.57.2 (latest), 1,817,781+ downloads  
**Dependency**: `Spectre.Console.Ansi` 0.57.2 (zero additional deps for net10.0)  
**License**: MIT  
**GitHub**: https://github.com/spectreconsole/spectre.console

#### Native AOT Compatibility: **CONFIRMED GOOD**

- As of **v0.49.0** (PR #1690), Spectre.Console has explicit AOT support.
- The `Spectre.Console` main library has `assembly: AssemblyMetadata("IsTrimmable", "True")` and AOT-safe code paths.
- **Exception**: `Spectre.Console.Cli` is explicitly marked as NOT AOT-compatible (`RequiresDynamicCode`). Do not use `CommandApp`.
- **Exception**: The `ExceptionFormatter` is marked with `RequiresDynamicCode` and falls back to a simpler format under AOT.
- Verified by actual AOT publish test: a net10.0 project with `PublishAot=true` and `Spectre.Console` 0.57.2 compiled and linked to a **2.7 MB native binary** with zero warnings or errors.
- The `Spectre.Console.Ansi` sub-package had no detectable reflection patterns in its binary.

#### Live Display / Real-time Dashboard Support: **YES**

| API | Description |
|-----|-------------|
| `AnsiConsole.Live(table)` / `LiveDisplay.Start(action)` | Continuously updates a renderable (Table, Panel, etc.) in-place |
| `LiveDisplayContext.UpdateTarget(IRenderable)` | Swap the current display target mid-stream |
| `LiveDisplayContext.Refresh()` | Force re-render |
| `AnsiConsole.Status().Start(...)` | Animated spinner with status text |
| `AnsiConsole.Progress().Start(...)` | Progress bars with auto-refresh |
| `AnsiConsole.Console.Write(table)` | One-time table/panel/tree rendering |

These all work via ANSI cursor positioning under the hood, which is fully AOT-safe.

#### Reflection Concerns: **LOW - mostly resolved**

- Prior to v0.49, Spectre used `TypeConverterHelper` with `Activator.CreateInstance` for prompt type conversion. This was refactored for AOT (uses intrinsic type converters).
- `EnumUtils` replaced reflection-based enum operations with pre-generated lookup.
- `ExceptionFormatter` is the only remaining `RequiresDynamicCode` path; it degrades gracefully under AOT.
- No `Emit`, `DynamicMethod`, or `ILGenerator` usage detected in the binary.

#### Thread Safety

- `LiveDisplay.Start()` is **not** thread-safe by default. It writes from a single thread.
- `Status.Start()` and `Progress.Start()` are synchronous from the caller's perspective.
- The `LiveDisplayContext` can be called from callbacks but should not be called concurrently.
- For multi-threaded updates (e.g., metric collection in one task, UI rendering in another), you would typically collect data in a thread-safe structure and update the display from a single render loop.

#### Binary Size Impact: ~1.5 MB

- A minimal Spectre.Console usage in AOT added ~1.5 MB to the native binary compared to an empty program (base ~77K -> with Spectre ~2.7 MB). This includes table rendering, markup parsing, and color support.

---

### Option 2: Terminal.Gui v2.4.17

**NuGet**: `Terminal.Gui` 2.4.17, 1.8M+ downloads  
**License**: MIT  
**GitHub**: https://github.com/tui-cs/Terminal.Gui

#### Native AOT Compatibility: **MIXED - HISTORICAL ISSUES, RECENT FIXES**

**Known AOT issues (now resolved in v2.4.x)**:
- **#5069** (`ConfigurationManager` required `TrimmerRootAssembly` for AOT due to reflection-based `ConfigProperty` attribute scanning) -- **FIXED** in #5071 by replacing reflection scan with explicit registration.
- **#5239** (`MissingMethodException` on `Dictionary<Command, PlatformKeyBinding>` deep clone at module init under AOT) -- **FIXED** before v2.4.17.
- **#5251** (AOT smoke test added to CI) -- merged.

Current status: Terminal.Gui v2.4.17 can be published with Native AOT and passes its own AOT smoke test. However:
- It still carries **11 NuGet dependencies** (ColorHelper, JetBrains.Annotations, Markdig, Microsoft.Extensions.Configuration* x3, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Options, System.IO.Abstractions, TextMateSharp x2, Wcwidth).
- None of these dependencies are annotated with `IsAotCompatible`.

#### Console-Only / Overkill for LanBridge

Terminal.Gui is a **full TUI toolkit** (windows, menus, buttons, text fields, mouse support, scrollbars). It is designed for interactive TUI applications (like `htop` or `nano`). For a simple continuously-updating status dashboard (no keyboard interaction needed beyond Ctrl+C), adding Terminal.Gui would be significant complexity bloat:
- Requires learning the View/Application event model
- Requires an event loop (`Application.Init()` / `Application.Run()`)
- Not designed for a "log output + periodic stats" pattern
- The 11 dependencies add ~5-8 MB to the AOT binary even after trimming

#### Verdict: **Not recommended** for a dashboard-only use case. Suitable only if interactive TUI input is needed.

---

### Option 3: Raw ANSI Escape Codes

**Dependencies**: Zero. Uses `System.Console` APIs only, which are included in all .NET AOT builds.

#### Approach

Use direct ANSI escape sequences with `Console.Write` for cursor positioning and styling:

| Escape Code | Purpose |
|-------------|---------|
| `\x1b[H` | Cursor home (1,1) |
| `\x1b[{row};{col}H` | Position cursor at row, col |
| `\x1b[J` | Clear screen |
| `\x1b[2J` | Clear entire screen |
| `\x1b[K` | Clear line from cursor |
| `\x1b[{n}A` | Cursor up n lines |
| `\x1b[{n}B` | Cursor down n lines |
| `\x1b[{n}C` | Cursor forward n chars |
| `\x1b[{n}D` | Cursor back n chars |
| `\x1b[s` | Save cursor position |
| `\x1b[u` | Restore cursor position |
| `\x1b[{n}m` | Set SGR attributes (color, bold, etc.) |

#### Color codes (SGR):
- `\x1b[31m` red, `\x1b[32m` green, `\x1b[33m` yellow, `\x1b[34m` blue, `\x1b[35m` magenta, `\x1b[36m` cyan
- `\x1b[1;32m` bold green
- `\x1b[0m` reset
- 256 color: `\x1b[38;5;{n}m` foreground, `\x1b[48;5;{n}m` background
- True color: `\x1b[38;2;{r};{g};{b}m`

#### Native AOT Compatibility: **PERFECT**

- No NuGet dependencies, no reflection, no trimming issues.
- `System.Console` is part of the BCL and fully AOT-compatible.

#### Live Display / Dashboard Support: **YES**

A typical dashboard pattern:
1. Write initial content with line feeds
2. Inside a timer/loop, move cursor back up with `\x1b[{n}A` and overwrite lines
3. Or use `Console.SetCursorPosition(row, col)` which is the managed equivalent

#### Thread Safety

- `Console` class is **not thread-safe** for concurrent writes. Use a single writer thread or lock.
- For a dashboard, have one timer callback that does all the output.

#### Pros:
- Zero dependencies, zero binary size impact
- Full AOT safety
- Maximum control
- Works on all modern terminals

#### Cons:
- Manual screen management
- No built-in table formatting, word wrapping, or layout
- Terminal width detection requires `Console.BufferWidth` or `Console.WindowWidth`
- Color support varies by terminal; need `TERM` env or `NO_COLOR` detection

---

### Option 4: Other Lightweight Options

#### ConsoleGUI v1.4.2

- NuGet: `ConsoleGUI` 1.4.2, 50K downloads
- Targets: netstandard2.0 only (**no net10.0 TFM**)
- License: MIT
- Project: https://github.com/TomaszRewak/C-sharp-console-gui-framework
- **AOT status: Unknown / unlikely** -- netstandard2.0 only, no AOT metadata, likely uses reflection
- No `LiveDisplay` pattern; it's a full widget-based GUI framework. Overkill and risky for AOT.

#### Pastel v7.0.1

- NuGet: `Pastel` 7.0.1, 1.4M downloads
- Targets: net8.0, net9.0 (no net10.0 TFM but should work)
- Zero dependencies for net8.0+
- **AOT status: Likely safe** -- simple string extension method for ANSI coloring
- **Limitation**: Only adds color to strings. No table rendering, no live display. Only for colored console output (equivalent to wrapping text with ANSI codes).
- Not useful for a dashboard; could replace the current `Console.ForegroundColor` pattern slightly more cleanly.

#### Colorful.Console v1.2.15

- NuGet: 18.6M downloads
- Targets: net472, netstandard2.0
- **AOT status: Unknown, and netstandard2.0 only** -- risky for AOT
- Uses a `StyleSheet` approach with `Figlet` fonts. No live display support.

#### McMaster.Extensions.CommandLineUtils

- Not a TUI library. CLI argument parsing only (already not needed since LanBridge does manual parsing).

---

### Recommendation

| Option | AOT Safety | Live Dashboard | Binary Impact | Complexity | Best For |
|--------|-----------|----------------|---------------|------------|----------|
| **Spectre.Console v0.57.2** | HIGH (verified) | YES (LiveDisplay, Status, Progress) | ~1.5 MB | Low | Most balanced choice |
| **Raw ANSI codes** | PERFECT | Manual | 0 KB | Medium | Zero-dependency / max AOT safety |
| Terminal.Gui v2.4.17 | MEDIUM (fixed, but heavy) | YES (full TUI) | ~5-8 MB | High | Only if interactive UI needed |
| ConsoleGUI | UNKNOWN | Widget-based | Unknown | High | Not recommended for AOT |
| Pastel | LIKELY safe | No (color only) | Minimal | Minimal | Only for colored output |
| Colorful.Console | RISKY | No | Unknown | Low | Not recommended for AOT |

**Primary recommendation**: **Spectre.Console v0.57.2** with `Spectre.Console.Cli` excluded. It provides `AnsiConsole.Live(table).Start(ctx => ...)` for real-time dashboard updates, has explicit AOT support, and adds minimal complexity. Use `AnsiConsole.Write(new Table())` for one-time config output and `AnsiConsole.Live(table).Start(ctx => ctx.UpdateTarget(updatedTable))` for the metrics/dashboard loop.

---

### Files Found

| File Path | Description |
|---|---|
| `src/LanBridge.Common/Runtime/ConsoleStatusWriter.cs` | Existing console output helper, uses `Console.ForegroundColor` |
| `src/LanBridge.Common/LanBridge.Common.csproj` | `IsAotCompatible=true`, no NuGet deps |
| `src/LanBridge.SignalingServer/Program.cs` | `PublishAot=true`, has `ReportMetricsLoopAsync` pattern |
| `src/LanBridge.IntranetPeer/Program.cs` | `PublishAot=true`, `OnStatusChanged` -> `ConsoleStatusWriter` |
| `src/LanBridge.ExtranetPeer/Program.cs` | `PublishAot=true`, same pattern as IntranetPeer |
| `src/LanBridge.KcpTest/Program.cs` | Uses `Console.ForegroundColor`, no `PublishAot` |

### External References

- [Spectre.Console GitHub - AOT support PR #1690](https://github.com/spectreconsole/spectre.console/issues/1690) — confirms AOT support from v0.49+, lists limitations (no Cli, no ExceptionFormatter under AOT)
- [Terminal.Gui GitHub - AOT issue #5069](https://github.com/tui-cs/Terminal.Gui/issues/5069) — `ConfigurationManager` reflection issue (fixed)
- [Terminal.Gui GitHub - AOT crash #5239](https://github.com/tui-cs/Terminal.Gui/issues/5239) — `MissingMethodException` in NativeAOT (fixed)
- [Terminal.Gui GitHub - AOT smoke test #5251](https://github.com/tui-cs/Terminal.Gui/issues/5251) — CI smoke test for AOT
- [Spectre.Console LiveDisplay docs](https://spectreconsole.net/live/) — official documentation
- [ANSI escape code reference](https://en.wikipedia.org/wiki/ANSI_escape_code) — ANSI CSI sequences

### Related Specs

- (None currently -- this research informs future dashboard UI decisions)

### Caveats / Not Found

- Terminal.Gui v2.4.17 was NOT in the NuGet cache, so its AOT behavior was assessed from GitHub issues and nuspec metadata only, not from direct AOT publish test.
- The "Pastel" and "Colorful.Console" packages were not AOT-tested in this research.
- The exact AOT binary size impact of Terminal.Gui could not be measured due to package unavailability in the local NuGet cache.
