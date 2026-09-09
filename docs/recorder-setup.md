# Recorder setup

Terminal Bloops reads process starts and exits from Microsoft's Sysmon event log. Its tray app runs as a normal Windows user; recorder installation requires a separate, explicit administrator setup.

[Project overview and build instructions](../README.md) · [Validation results](../VALIDATION.md)

## Current validation limit

On the development machine, Sysmon 15.21 accepted the generated schema 4.91 configuration but failed to register its Windows Event Log manifest: `wevtutil.exe returned failure`, exit code **13**. No Sysmon service or event channel was installed, so persistent process recording is **not active on that machine**. The Windows/provider-registration failure remains unresolved.

Setup can be rerun after that issue is corrected. Successful builds, automated tests, and UI previews do not establish that live recording works. See the [validation record](../VALIDATION.md) for the completed checks and the deployment limit.

## Set up from the app

1. Build and publish using the [README](../README.md), then run `artifacts\publish\TerminalBloops.exe` as your normal Windows user. Keep the complete publish folder together; it includes the runtime and the `scripts` folder.
2. Open the recorder settings and choose **Set up recorder**.
3. Approve the Windows UAC prompt. The application itself continues without administrator privileges.
4. Check the recorder status. A successful setup must produce a running recorder and a readable event channel; a recovered failed attempt is not an installed recorder.

A new install requires internet access. Setup downloads Sysmon from Microsoft's official Sysinternals URL and verifies a valid Authenticode signature with organization **Microsoft Corporation** and publisher **Microsoft Corporation** or **Microsoft Windows Publisher**. It accepts the Sysmon EULA only during the explicit installation operation. Building the project does not download or install Sysmon.

### What a new installation records

The configuration enables process creation and termination, Sysmon event IDs **1** and **5**, system-wide. This lets the app connect terminals to their launchers and recover available process history after the app was closed. Other configurable Sysmon event classes are disabled. Sysmon's own service, configuration, and error diagnostics cannot all be filtered out.

Setup derives the filters from the verified binary's schema and also disables newly introduced non-process event classes. A new installation uses a **256 MiB circular Windows event log**: the oldest entries are overwritten when it fills.

### If Sysmon is already installed

Setup preserves the existing installation, capture configuration, and retention settings. It adds only a read access entry for the requested Windows user to the Sysmon event channel, preserving other access entries. It does not add the user to the broader **Event Log Readers** group.

The existing administrator's rules must capture process-create and process-terminate events for a complete timeline. Setup reports a stopped service or disabled channel without changing it. It never overwrites a managed Sysmon configuration.

## Manual setup

Run these commands from the repository root or the complete published application folder. Elevated helpers require the built-in **Windows PowerShell 5.1** (`powershell.exe`), not PowerShell 7 (`pwsh.exe`).

First, obtain the intended user's SID and data directory in a **normal, unelevated PowerShell window**:

```powershell
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Join-Path $env:LOCALAPPDATA 'TerminalBloops'
```

Then open **Windows PowerShell as administrator** and substitute those exact values:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup-Recorder.ps1 -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

Use the original user's values even if elevation uses a different administrator account. The helper validates the data directory against that SID's registered Windows profile. It deliberately rejects redirected profile paths outside `AppData\Local` and paths containing junctions or symbolic links.

`-ExecutionPolicy Bypass` applies only to the invoked process so the local unsigned helper can run. It does not change the saved Windows execution policy or override organization policy. No elevation is needed to build or publish.

## Privacy and permissions

Process command lines can contain tokens, passwords, file paths, and other private data. History stays on the computer; no cloud service or telemetry is required. Review raw history or diagnostics before sharing them.

The app's SQLite history is stored at `%LOCALAPPDATA%\TerminalBloops\history.db`. Keep copied databases, event-log exports, and diagnostic reports out of a public repository; the project's ignore rules exclude their usual runtime filenames.

**The Sysmon read permission covers its machine-wide event channel.** It can expose other users' process command lines too. The app's current-user view is a display filter, not an event-log security boundary.

Protected ownership and reader-grant metadata live in `%ProgramData%\TerminalBloops\Recorder`, writable only by Administrators and SYSTEM. Per-user result files are status messages, never proof of installation ownership.

Setup downloads into a protected temporary staging directory. After signature verification, it grants only the EventLog service SID and LocalService read/execute access to that attempt's staging resources. Writes remain restricted to Administrators and SYSTEM; other metadata permissions are unchanged. The staging directory is removed after the attempt.

## Diagnose a failure

From administrator Windows PowerShell, use the same SID and data-directory values:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Diagnose-Recorder.ps1 -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

This helper reads recorder state without installing, repairing, or reconfiguring it. It writes `recorder-diagnostics.json` into the requested user's data folder with ownership flags, relevant service/channel state, TEMP/TMP paths, and the recorder helper's own installer diagnostics.

| Location | Contents |
| --- | --- |
| `%LOCALAPPDATA%\TerminalBloops\setup-result.json` | Setup or recovery outcome, operation mode, message, and exit code |
| `%LOCALAPPDATA%\TerminalBloops\remove-result.json` | Removal outcome |
| `%LOCALAPPDATA%\TerminalBloops\recorder-diagnostics.json` | Bounded diagnosis report |
| `%ProgramData%\TerminalBloops\Recorder\last-setup-native-error.txt` | Administrator-only native setup diagnostics |
| `%ProgramData%\TerminalBloops\Recorder\last-removal-native-error.txt` | Administrator-only native removal diagnostics |

Native failures also retain exact `.stdout.bin` and `.stderr.bin` streams beside the protected error file for encoding analysis. They are not included in normal app history. Full diagnostics remain administrator-only until the diagnosis helper explicitly copies its bounded report to the user folder.

### Common failures

| Symptom | Next step |
| --- | --- |
| UAC was cancelled | Reopen setup when ready. This attempt did not enable persistent recording. |
| No event channel or access denied | Read `setup-result.json` and check **Event Viewer → Applications and Services Logs → Microsoft → Windows → Sysmon → Operational**. Retry for the same user. Group Policy and explicit deny entries are preserved. |
| Existing Sysmon, missing activity | Ask its administrator to verify a running service and event IDs 1 and 5 in the capture rules. Setup will not replace those rules. |
| Signature or download failure | Check connectivity and the Windows clock. The helper will not execute a binary that fails signature validation. |
| Metadata ownership error | The protected recorder directory must be administrator-owned with no non-administrator write access. Setup refuses to trust an insecure pre-created directory. |
| Manifest registration fails with exit 13 | Collect diagnostics. This remains unresolved on the development machine; broader Windows/provider changes are not part of setup. |
| Setup was interrupted | Use the limited recovery procedure below. Do not delete ownership metadata to force installation or removal. |

Helper exit codes are **0** success, **10** elevation/path/ownership or pre-existing health failure, **20** download/signature failure, **30** installation/removal failure, and **40** permission or safe-removal verification failure. A result may include the installer's concise error summary; the full native output is retained separately.

## Recover an interrupted setup

A retry for the same owner can clear a pending attempt only when no service, driver, channel, or binary remains. If its sole leftover is `C:\Windows\Sysmon64.exe`, setup verifies its Microsoft signature, equality to a fresh verified download, creation after the recorded setup began, and absence of service/driver references. It then preserves that file under the protected `Recorder\recovery` directory and records the backup before proceeding. Other remnants and established installations remain untouched.

To perform **only that recovery**, without attempting installation:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup-Recorder.ps1 -RecoverOnly -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

Recovery still downloads and verifies the official binary for comparison. It never invokes installation or adds a reader access entry, and it refuses to change an existing service, channel, or established owned installation. Success uses `mode: "recovery-only"` and explicitly states that the recorder is **not installed**. Ordinary installation failures also attempt this limited backup while their verified download remains available.

## Remove the recorder

Exit the tray app, then run the removal helper explicitly from administrator Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Remove-Recorder.ps1 -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

Removal uninstalls Sysmon only when protected metadata establishes that Terminal Bloops installed it and the service executable, Microsoft signature, binary hash, and capture configuration still match. It refuses to uninstall an updated or reconfigured recorder or one still shared by other Terminal Bloops readers.

For a pre-existing Sysmon installation, removal deletes only the exact read access entry the helper added for this user. It preserves unrelated permissions, configuration, and local app history. It does not erase the Windows event log or delete the application folder.

## What history can and cannot show

- Process logging can catch very short runs, but window observation works only while the app is running on that interactive desktop. A hidden process, another session, or a missed window event can remain **visibility unconfirmed**.
- An observed terminal window does not establish which command ran in a shared Windows Terminal pane. Unresolved command attribution stays labelled as such.
- Process metadata does not recover terminal output or prove whether a command is harmless or malicious.
- Sysmon cannot reconstruct events from before installation or after circular-log overwrite. A checkpoint can detect a replay gap but cannot restore overwritten records.

## Keep a terminal open for inspection

For Windows Terminal-hosted windows, open **Settings → Profiles → Defaults → Advanced → Profile termination behavior → Never close automatically**. Its JSON setting is `profiles.defaults.closeOnExit = "never"`. Set **Startup → Default terminal application → Windows Terminal** for eligible externally launched console applications to use it. [Microsoft's termination settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced), [default terminal settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/startup).

This preserves the display after a process ends; it does not create a durable recorder history. Classic console windows and application-controlled windows are outside that setting. Terminal Bloops does not change terminal behavior.

For a known command you control, `cmd /k` or PowerShell `-NoExit` keeps the shell running. These switches change process lifetime and can affect automation waiting for completion. [Command Prompt reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd), [PowerShell executable reference](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_powershell_exe?view=powershell-5.1).

For Sysmon's installation options and event descriptions, see [Microsoft's Sysmon documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon).
