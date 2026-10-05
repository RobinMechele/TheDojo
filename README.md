# The Dojo

> *"I'm trying to free your mind, Neo. But I can only show you the door. You're the one that has to walk through it."*

A Windows desktop app (and CLI) that analyses how you use AI coding agents, **Claude Code** and **GitHub Copilot CLI**, so you can learn from your token use, spend and behaviour and get better at it over time.

It reads the session logs your agents already write on this machine (nothing to install in the agents, no proxy), prices every model call, checks your live plan limits, and turns it all into reports and concrete lessons.

## What you get

| Page | What's on it |
|---|---|
| **Overview** | Spend and trend, spend per day by agent, Dojo score and belt, live plan limits, key figures, top lessons, spend by model and project |
| **Coach** | Every lesson with what was seen, what to try and what it could save; how the score is made |
| **Sessions** | Every session, searchable and sortable; click one for its context-growth chart, prompts with their cost, tools and errors |
| **Models** | Calls, tokens, cache hit, average context and spend per model; spend by context size and effort; why replies ended |
| **Projects** | Spend, prompts, lines changed and PRs per repository (worktrees fold into their repo) |
| **Tools & agents** | Tool calls with failure rate and duration, shell programs, subagents, MCP servers, skills, slash commands, errors |
| **Activity** | Weekday × hour heatmap, prompts over time, waiting time, the most expensive prompts |
| **Limits** | Live Claude session/weekly and Copilot windows with pace, and how they filled up over time |
| **Reports** | Markdown or JSON report of the period (prompt text optional), CSV of calls, sessions and tools |

The period (24h, 7d, 30d, 90d, all), agent and project filters at the top apply to every page. F5 refreshes.

Everything it tracks, and where each number comes from, is in **[docs/DATA-MAP.md](docs/DATA-MAP.md)**.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project src/TheDojo                 # the app
dotnet run --project src/TheDojo -- --offline    # no network: built-in prices, no live limits
```

**CLI** (`dojo`), for terminals, scripts and scheduled reports:

```powershell
dotnet run --project src/TheDojo.Cli -- summary --range 7d
dotnet run --project src/TheDojo.Cli -- report --range 30d --out report.md
dotnet run --project src/TheDojo.Cli -- report --format json --prompts --out report.json
dotnet run --project src/TheDojo.Cli -- export calls --range 90d --out calls.csv
dotnet run --project src/TheDojo.Cli -- limits
```

Options: `--range 24h|7d|30d|90d|all`, `--provider claude|copilot`, `--project NAME`, `--claude-home DIR`, `--copilot-home DIR`, `--cache DIR`, `--offline`.

**Self-contained build:** `powershell -File build/publish.ps1` runs the tests and publishes the app and CLI to `artifacts/`.

## Privacy

- Logs are read in place. Prompt text stays on this machine. Reports leave it out unless you tick "Include prompt text" (CLI: `--prompts`).
- Plan limits use the sign-ins your CLIs already have. The Claude token only goes to Anthropic. The Copilot quota comes from the Copilot CLI itself, and a GitHub token is only used as a fallback and only sent to GitHub.
- Caches and limit history live in `%LocalAppData%\TheDojo` (`DOJO_HOME` moves it).

## Costs

Claude Code calls are priced at Anthropic's public API rates per token kind, including the 5-minute and 1-hour cache-write tiers and fast mode. These estimates match Claude Code's own per-session cost. Copilot calls use the AI credits the CLI reports. On a subscription plan you don't pay per token: treat the dollars as a measure of how much work you asked for, and use the Limits page for what you actually have left.

## Tests

```powershell
dotnet test tests/TheDojo.Tests        # parsers, pricing, analysis, coach, limits, reports, CLI end-to-end
dotnet test tests/TheDojo.UiTests      # local only: every page off-screen, binding errors, screenshot baselines
$env:DOJO_REAL_DATA=1; dotnet test     # also against this machine's real logs (cost check + screenshots in %TEMP%\dojo-real-screens)
```

## Project layout

```
src/TheDojo.Core      UI-free: parsers (Ingest), pricing, plan limits, analysis, coach (Insights), reports
src/TheDojo           WPF app (MVVM): pages, charts drawn in-app, theme
src/TheDojo.Cli       dojo command line
tests/TheDojo.Tests   xUnit: unit + end-to-end on a generated "sample world" of agent logs
tests/TheDojo.UiTests In-process UI tests and screenshot baselines (Snapshots/)
docs/DATA-MAP.md      Every tracked field and its source
```
