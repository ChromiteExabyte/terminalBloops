# Verification

The .NET 10 Windows build and 70 automated tests pass. Tests cover process duration boundaries (including submillisecond precision), transactional checkpoints, replay deduplication, out-of-order events, missing exits, PID reuse, exact window attribution, retention, parent chains, filtering, notification policy, Sysmon parsing, PE classification, producer health, and cleared-log checkpoint detection.

Windows PowerShell 5.1 helper tests cover ACL preservation, install ownership, atomic metadata writes, safe interrupted-install recovery, native-output encoding, and generated Sysmon filtering configuration. They do not change machine settings.

The self-contained application's UI smoke check verifies that it has no console window, cards do not activate or steal foreground focus, notification bursts produce at most three cards plus an overflow counter, and cards expire after six seconds. It also checks that event details and drawers start collapsed, only one event opens at a time, closing a row clears selection, programmatic event selection opens its details, filters still update the view model, and search/settings drawers open individually. Rendered checks cover light, dark, high-contrast, compact, collapsed, expanded, search, settings and empty views, plus each notification theme and the overflow card. The minimal Frutiger Aero redesign was reviewed at 860 × 660 and the 660 × 500 minimum size. Text selection uses paired theme colors, interactive controls have distinct outlines, and high contrast uses corresponding Windows foreground/background colors.

## Continuous integration

The [Windows build workflow](https://github.com/ChromiteExabyte/terminalBloops/actions/workflows/windows.yml) runs the Release build, 70 automated tests, Windows PowerShell recorder-helper checks, and self-contained Windows x64 publish on a clean GitHub-hosted Windows runner. Successful runs upload a `TerminalBloops-win-x64` development package for 14 days, including the recorder helpers and documentation. The workflow has read-only repository permissions and uses pinned action revisions.

CI does not install Sysmon, exercise live event capture, or run interactive UI focus checks. Those checks require a configured interactive Windows desktop and remain separate. The package is unsigned and is not presented as a stable release.

## Reproduce locally

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
Start-Process .\artifacts\publish\TerminalBloops.exe -ArgumentList '--self-test', 'C:\path\to\ui-check-output' -Wait
```

The following explicit diagnostics require a working Sysmon installation. They do not modify startup settings or the real application database. Run them as the normal Windows user to verify channel permissions:

```powershell
Start-Process .\artifacts\publish\TerminalBloops.exe -ArgumentList '--capture-test', 'C:\path\to\capture-check-output' -Wait
Start-Process .\artifacts\publish\TerminalBloops.exe -ArgumentList '--window-test', 'C:\path\to\window-check-output' -Wait
```

The window test deliberately displays one identified terminal for approximately two seconds. Diagnostic JSON contains only controlled fixture evidence. Windows Terminal pane attribution, secure desktops, and other interactive sessions are intentionally outside confirmed window coverage.

## This machine's deployment result

The official, signature-verified Sysmon 15.21 download validates the generated configuration, but Windows' `wevtutil.exe` fails during event-manifest registration. Sysmon exits with code 13. This persisted with redirected and unredirected hidden launches and explicit read access for the event-log service to installer resources.

No Sysmon service or event channel was installed, so **persistent recording is not active** and the live capture/window diagnostics could not be completed. Unit and UI test success does not establish live capture. The setup helper can be rerun after the Windows registration failure is resolved; it does not alter unrelated event providers or existing recorder configurations.

The native error and installation ownership metadata are preserved under `%ProgramData%\TerminalBloops\Recorder`. The elevated diagnosis helper can copy a bounded report into the current user's `%LOCALAPPDATA%\TerminalBloops\recorder-diagnostics.json`. Failed, unregistered installer binaries are preserved in a protected recovery backup rather than left as an active installation.
