# flowboost

flowboost is a Windows .NET 10 tray app that binds Logitech MX Keypad hardware keystrokes to GitHub Copilot prompts. It copies the current text selection, sends it with the selected preset, and displays the streamed response in a popup.

Repository: [github.com/ydakilux/flowboost](https://github.com/ydakilux/flowboost)

## Versioning and release notes

flowboost uses [Semantic Versioning](https://semver.org/) in `MAJOR.MINOR.PATCH` format. The current app version is **0.1.0**; the app version and release notes are available from Settings. Release notes are maintained in [CHANGELOG.md](CHANGELOG.md).

## Build and run

Install PowerShell 7 or later and the .NET 10 SDK, then publish from the repository root:

```powershell
./build.ps1
```

The script publishes the framework-dependent app to `publish/flowboost.exe`. The target machine must have the .NET 10 Desktop Runtime installed. If publishing reports that the output is locked, close any running `publish/flowboost.exe` and retry; the script will not stop it for you.

Run the tests from the repository root:

```powershell
dotnet test flowboost.slnx
```

The Copilot SDK runtime is embedded and extracted by the .NET single-file host when needed.

Run `src/flowboost/bin/Debug/net10.0-windows/win-x64/flowboost.exe`. The app stays in the notification area; choose **Settings** or **Quit** from its tray menu. Only one instance can run per Windows session: launching it again while it is already running exits without opening another tray icon. Quit the existing instance before starting a different build.

## GitHub sign-in

flowboost uses GitHub OAuth device flow with the application's public OAuth client ID embedded in the app. No separate GitHub Copilot CLI installation is needed. The GitHub OAuth App owner must enable **Device Flow** in the app's settings. Users simply click **Sign in** in flowboost, follow the displayed GitHub browser authorization URL, and approve the request.

OAuth tokens are protected with Windows DPAPI and stored below `%AppData%\flowboost`. Existing credentials for the standalone GitHub Copilot CLI are left untouched. A live GitHub sign-in has not been verified as part of this release documentation.

Settings and flowboost's Copilot working data are stored below `%AppData%\flowboost`; existing legacy settings fields are ignored and are not rewritten until settings are saved.

When an operation fails, flowboost appends a diagnostic entry to `%AppData%\flowboost\log.txt`. Entries contain a UTC timestamp, a fixed event identifier, and (when available) the exception type. The log is append-only; it does not include exception messages or stacks, Copilot responses, prompts, clipboard/selection contents, or authentication tokens. Treat the file as private diagnostic data and review it before sharing.

## Logitech Options+

In Logi Options+, assign the MX Keypad keys to **Keystroke** actions with these shortcuts:

| Preset | Keystroke |
| --- | --- |
| TLDR | Ctrl+Alt+Shift+T |
| Explain | Ctrl+Alt+Shift+E |
| Translate | Ctrl+Alt+Shift+R |
| Fix grammar | Ctrl+Alt+Shift+G |

The presets and shortcuts can be changed in Settings. Select text before pressing a shortcut.
