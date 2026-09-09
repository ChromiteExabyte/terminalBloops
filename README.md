# Terminal Bloops

A quiet Windows 11 tray app for understanding terminal windows that appear and disappear. A local timeline connects short process runs to their command and launcher. Observed windows are labelled separately from process events whose visibility could not be confirmed; a brief run alone is not evidence of malware.

The interface uses a quiet Frutiger Aero palette: soft sky and green tones, glass panels and a glossy terminal orb. The main view is a single list; expand an event to see its command, launcher, window evidence and ancestry. Search, filters and recorder preferences stay in collapsible drawers. Light, dark and high-contrast colors follow Windows; notification cards use the same styling and never take keyboard focus. Terminal Bloops is an independent application.

**Live validation status on this machine (September 8, 2026):** Sysmon 15.21 accepts the generated schema 4.91 configuration, but its Windows Event Log manifest registration fails with exit 13 (`wevtutil.exe returned failure`). No Sysmon service or event channel was installed. Live process recording is therefore **not active** here. This Windows/provider-registration problem remains unresolved; setup can be rerun after that external issue is corrected. Building the app or passing its local tests does not establish that recording is running.

## Run

Publish the app using the instructions below, then run `artifacts\publish\TerminalBloops.exe` as your normal Windows user. Keep the complete publish folder together. The app needs no separate .NET installation. Windows x64 is supported.

Use **Set up recorder** for the one-time elevated setup. Approve Windows' UAC prompt. The app itself continues without administrator privileges. Setup downloads Sysmon from Microsoft's official Sysinternals URL and verifies a valid Authenticode signature with organization Microsoft Corporation and publisher Microsoft Corporation or Microsoft Windows Publisher. It accepts the EULA only when this explicit setup runs. Internet access is required for a new install. No download or installation occurs just by building the project.

The recorder captures process starts and exits system-wide so the app can connect terminals to their launchers, including processes that finish while its window is closed. A new installation uses a 256 MiB circular Windows event log; oldest entries are overwritten when full. Other configurable Sysmon event classes are disabled. Sysmon's own service, configuration and error diagnostics cannot all be filtered out.

If Sysmon already exists, setup preserves its installation, capture rules and log retention. It adds only a read ACE for the requested Windows user to the Sysmon event channel, preserving the other ACEs; it does not add the user to Event Log Readers. The existing administrator's rules must capture event IDs 1 and 5 for a complete timeline. A stopped service or disabled channel is reported without changing it.

Process command lines can contain tokens, passwords and file paths. Data stays on this computer. Sysmon's channel contains events for the machine, so its read permission permits reading other users' process command lines too; the app's current-user view is not an event-log security boundary. Do not share raw history or setup diagnostics without reviewing them. No cloud service or telemetry is required.

## Build and publish

Install the .NET 10 SDK, or place a compatible SDK in `.tools\dotnet`. The scripts prefer that workspace SDK. `global.json` selects the .NET 10 feature band with roll-forward enabled.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

Build creates the WPF app and runs the test project when present. Publish creates `artifacts\publish`, including the .NET runtime and the elevated setup/removal helpers in `scripts`. This is a folder deployment, not an installer; distribute the entire folder. Source helpers and their shared script must remain together. No elevation is needed for build or publish. `-ExecutionPolicy Bypass` applies only to the invoked PowerShell process so these local unsigned scripts can run; it does not change Windows' saved execution policy or override organization policy.

## Manual recorder setup and removal

The app supplies the current user's SID and `%LOCALAPPDATA%\TerminalBloops` to Windows PowerShell 5.1 using `RunAs`. For manual use, first note the following values in a normal, unelevated PowerShell window:

```powershell
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Join-Path $env:LOCALAPPDATA 'TerminalBloops'
```

In an **administrator Windows PowerShell** window, substitute those exact values:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Setup-Recorder.ps1 -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

To remove the recorder, exit the tray app, then explicitly run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Remove-Recorder.ps1 -UserSid 'S-1-5-21-REPLACE' -DataDirectory 'C:\Users\YOUR_USER\AppData\Local\TerminalBloops'
```

Removal uninstalls Sysmon only when protected metadata proves Terminal Bloops installed it and the service executable, Microsoft signature, binary hash and capture configuration still match. For a pre-existing Sysmon installation, it removes only the exact reader ACE it added for this user. It preserves unrelated permissions and capture settings. It refuses to uninstall if another user still has a Terminal Bloops reader grant, or if an administrator has updated or reconfigured the owned recorder. App history is retained. Removal does not erase the Windows event log.

Protected ownership and reader-grant metadata are in `%ProgramData%\TerminalBloops\Recorder`, writable only by Administrators and SYSTEM. Per-user setup results are in `%LOCALAPPDATA%\TerminalBloops\setup-result.json` and `remove-result.json`. These user-writable result files are status messages, never proof of install ownership. Setup downloads into a protected temporary directory. After signature verification, it grants only the EventLog service SID and LocalService read/execute access to that attempt's staging directory and files for provider-resource registration. Administrator/SYSTEM-only write access remains in place, other metadata ACLs are unchanged, and staging is removed after the attempt. If Sysmon reports an error, native diagnostics remain in protected `last-setup-native-error.txt` or `last-removal-native-error.txt` for an administrator to inspect.

For a read-only recorder diagnosis, run `Diagnose-Recorder.ps1` from administrator Windows PowerShell using the same `-UserSid` and `-DataDirectory` arguments. It writes `recorder-diagnostics.json` into that user's data folder with ownership flags, relevant service/channel status and Terminal Bloops' own installer diagnostics. It does not install, repair or reconfigure anything. Native failure streams are also retained as protected `.stdout.bin`/`.stderr.bin` files for encoding analysis; they are not included in normal app history.

To recover only an interrupted installation's verified orphan files, use `Setup-Recorder.ps1 -RecoverOnly` with the same elevated `-UserSid` and `-DataDirectory` arguments. It downloads and verifies the official binary for comparison, preserves eligible remnants in the protected recovery folder and clears only this user's safe pending attempt. It never invokes installation or adds a reader ACL, and refuses to change an existing service, channel or established owned installation. A successful result has `mode: "recovery-only"` and explicitly states that the recorder is **not installed**. Ordinary installation failures also attempt this limited backup while the verified download is still available.

## Troubleshooting and limits

- **UAC cancelled:** recording setup did not run. Reopen the app and use its setup action when ready.
- **No event channel / access denied:** inspect `setup-result.json`, rerun setup for the same user, and check **Event Viewer → Applications and Services Logs → Microsoft → Windows → Sysmon → Operational**. Group Policy or an explicit deny ACE may prevent access; setup preserves those restrictions.
- **Existing Sysmon but missing activity:** ask its administrator to confirm process-create and process-terminate rules and a running service. Terminal Bloops will not overwrite managed settings.
- **Signature or download failure:** retry with internet access and an accurate Windows clock. Setup never executes a binary that fails Microsoft Authenticode validation.
- **Setup interrupted:** metadata is written before installation and permission changes. A retry for the same owner automatically clears a pending attempt when no service, driver, channel or binary remains. If its sole leftover is `C:\Windows\Sysmon64.exe`, setup verifies the signature, fresh-download hash, creation time and absence of service/driver references before preserving it in the protected `Recorder\recovery` folder and retrying. Other footprints and established installations remain untouched. Do not delete protected metadata to force removal.
- **Metadata ownership error:** the ProgramData recorder directory must be administrator-owned with no non-administrator write access. Setup refuses to trust a pre-created insecure directory.
- **No window observed:** process logging can survive very short runs, but desktop window observation only works while the app is running in that interactive session. A hidden process, another session, or a missed window notification may remain visibility-unconfirmed. Keeping process metadata does not recover terminal output.
- **Older history unavailable:** Sysmon cannot reconstruct events from before installation or after circular-log overwrite. A collector checkpoint can identify replay gaps but cannot recover overwritten events.
- **PowerShell edition:** elevated helpers require the built-in `powershell.exe` (Windows PowerShell 5.1); build/publish can also run under PowerShell 7. Profile paths redirected away from `AppData\Local` and directory junctions are deliberately rejected by the elevated helper.

Helper exit codes are `0` success, `10` elevation/path/ownership or pre-existing health failure, `20` download/signature failure, `30` installation/removal failure, and `40` permission or safe-removal verification failure. Result JSON contains a concise message and, when present, the installer's error summary; full native diagnostics remain administrator-only. Setup builds the event filters from the verified binary's own schema and disables newly introduced non-process event classes as well.

## About keeping terminals open

For Windows Terminal-hosted windows, **Settings → Profiles → Defaults → Advanced → Profile termination behavior → Never close automatically** preserves the display after a process ends (`profiles.defaults.closeOnExit = "never"`). Choose Windows Terminal as the default terminal application in its Startup settings for eligible external launches. This is separate from recorder setup; Terminal Bloops does not change terminal behavior. Classic console windows and application-controlled windows are outside that setting. For a command you control, `cmd /k` or PowerShell `-NoExit` keeps its shell running, which can affect automation waiting for completion.

Sources: [Microsoft Sysmon documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon), [Windows Terminal termination behavior](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-advanced), [default terminal settings](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/startup).
