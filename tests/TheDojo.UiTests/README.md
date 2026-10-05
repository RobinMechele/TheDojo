# In-process UI tests

The real WPF window and pages, built off-screen inside the test process on `SampleWorld` (a generated month of Claude Code and Copilot CLI logs), with a fixed clock (UTC), built-in prices and fixed plan limits. The whole run takes about 5 seconds. Local only: `[UiFact]` skips when `CI=true`.

```
dotnet test tests/TheDojo.UiTests
$env:DOJO_UPDATE_SNAPSHOTS=1; dotnet test tests/TheDojo.UiTests   # accept intended UI changes
$env:DOJO_REAL_DATA=1; dotnet test tests/TheDojo.UiTests          # also render every page on this machine's real logs to %TEMP%\dojo-real-screens
```

- **ScreenTests**: a screenshot of every page and the session detail, compared with `Snapshots/*.png`. A mismatch writes `<name>.actual.png` and `<name>.diff.png` (changed pixels in red).
- **FlowTests**: navigation, filters reloading every page, session search and detail, report exports and the clipboard, and the heatmap metric switch.
- Any WPF binding error fails the test.
