# AGENTS.md

The Dojo is a WPF desktop app plus a CLI (.NET 10) that analyses Claude Code and GitHub Copilot CLI usage from their own session logs: tokens, cost, limits and behaviour, turned into reports and coaching.

## Layout
- `src/TheDojo.Core` (net10.0, no UI):
  - `Ingest/`: the log parsers (`ClaudeTranscriptParser`, `CopilotEventsParser`) and the cached `LogScanner`.
  - `Model/`: provider-neutral events (`ApiCall`, `ToolCall`, `PromptEvent`, …) and `UsageDataset` (merge + de-duplication).
  - `Pricing/`: `ClaudePricing` built-in rates and `PriceBook` (LiteLLM).
  - `Limits/`: live plan limits and the limit history.
  - `Analysis/DojoAnalyzer`: every report number.
  - `Insights/InsightEngine`: lessons and the score.
  - `Reports/`: Markdown, JSON and CSV.
  - `DojoService`: orchestration.
- `src/TheDojo`: WPF app (MVVM, CommunityToolkit.Mvvm). One view model per page in `ViewModels/`, views in `Views/`. Charts are drawn in `Controls/` (`OnRender`, no chart library).
- `src/TheDojo.Cli`: `dojo` command (`DojoCli`).
- `tests/TheDojo.Tests`: xUnit. `Fixtures/` writes realistic Claude/Copilot logs (`ClaudeLog`, `CopilotLog`), and `SampleWorld` writes a month of them.
- `tests/TheDojo.UiTests`: the real window off-screen on `SampleWorld`. Binding errors fail the test, and screenshots are compared with `Snapshots/`.
- `docs/DATA-MAP.md`: every tracked field and its source. Keep it current when a parser reads something new.
- `src/TheDojo.Core/Updates/UpdateService`: checks the latest GitHub release and swaps the exe (checksum-verified); `UpdateViewModel` is the banner. The release asset names (`TheDojo-win-x64.exe`, `SHA256SUMS.txt`) are a contract with `build/publish.ps1` and `.github/workflows/release.yml`.

## Commands
```
dotnet build TheDojo.slnx
dotnet test tests/TheDojo.Tests
dotnet test tests/TheDojo.UiTests                                   # local only
$env:DOJO_UPDATE_SNAPSHOTS=1; dotnet test tests/TheDojo.UiTests     # accept intended UI changes (look at Snapshots/*.png first)
$env:DOJO_REAL_DATA=1; dotnet test                                  # also check against this machine's real logs
dotnet run --project src/TheDojo.Cli -- summary --range 7d
powershell -File build/publish.ps1
```

## Rules
- Numbers are computed in Core (`DojoAnalyzer`, `InsightEngine`), never in view models. View models only format.
- Anything time-based takes a `TimeProvider`; tests use `FakeTimeProvider`. Dates shown in the UI go through `Display` (the app clock's time zone, English names).
- Parsers are tolerant: agent log schemas change with every CLI release. Unknown fields are ignored, a broken line is skipped, and nothing throws on bad input.
- When a parser starts reading something new, bump `LogScanner.CacheVersion` so cached results are re-parsed.
- Event ids must stay stable across copies of the same history: resumed or forked sessions copy lines into new files, and `UsageDataset` de-duplicates on id. Copilot ids are scoped by session because the CLI reuses them.
- Tokens for plan limits are only ever sent to their own service (Anthropic, GitHub). Prompt text never leaves the machine and is left out of reports unless asked for.
- Charts follow the dataviz rules: colour follows the entity (Claude orange, Copilot blue), one axis, hairline grids, tooltips on hover, and status colours always come with an icon and a label.
- Keep the UI dark, minimal and English. No file paths in the UI except where they are the point (e.g. a re-read file in a lesson).
