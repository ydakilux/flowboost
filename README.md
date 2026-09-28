# flowboost

flowboost is a Windows .NET 10 tray app that binds keyboard shortcuts to GitHub Copilot prompts. Select text in any application, press a shortcut, and flowboost copies the selection, sends it with the chosen preset, and streams the response in a popup where you can keep chatting with Copilot.

Any keyboard works. A Logitech MX Keypad (or any programmable keypad/macro key) is optional: map its keys to the same shortcuts to trigger presets with a single press.

Repository: [github.com/ydakilux/flowboost](https://github.com/ydakilux/flowboost)

## Versioning and release notes

flowboost uses [Semantic Versioning](https://semver.org/) in `MAJOR.MINOR.PATCH` format. The current app version is **0.1.1**; the app version and release notes are available from Settings. Release notes are maintained in [CHANGELOG.md](CHANGELOG.md).

## Updates

flowboost checks GitHub Releases at startup and on request via **Settings > Check for updates**. When an update is available, the app shows a dialog with the release information and a link to its release page. flowboost never downloads or installs updates automatically.

To publish an update, maintainers must create a GitHub Release with a tag in `vMAJOR.MINOR.PATCH` format matching the `<Version>` in `src/flowboost/flowboost.csproj`, and attach the published `publish\flowboost.exe`.

## Build and run

Install PowerShell 7 or later and the .NET 10 SDK, then publish from the repository root:

```powershell
./build.ps1
```

The script publishes the framework-dependent app to `publish\flowboost.exe` in the repository root (the `publish` folder is created if needed and is ignored by Git). The target machine must have the .NET 10 Desktop Runtime installed. If publishing reports that the output is locked, quit any running `publish\flowboost.exe` and retry; the script will not stop it for you.

Run the tests from the repository root:

```powershell
dotnet test flowboost.slnx
```

The Copilot SDK runtime is embedded and extracted by the .NET single-file host when needed.

Run `publish\flowboost.exe` (or, for a debug build, `src\flowboost\bin\Debug\net10.0-windows\win-x64\flowboost.exe`). The app stays in the notification area; choose **Settings** or **Quit** from its tray menu. Only one instance can run per Windows session: launching it again while it is already running exits without opening another tray icon. Quit the existing instance before starting a different build.

## GitHub sign-in

flowboost uses GitHub OAuth device flow with the application's public OAuth client ID embedded in the app. No separate GitHub Copilot CLI installation is needed. The GitHub OAuth App owner must enable **Device Flow** in the app's settings. Users simply click **Sign in** in flowboost, follow the displayed GitHub browser authorization URL, and approve the request.

OAuth tokens are protected with Windows DPAPI and stored below `%AppData%\flowboost`. Existing credentials for the standalone GitHub Copilot CLI are left untouched. A live GitHub sign-in has not been verified as part of this release documentation.

Settings and flowboost's Copilot working data are stored below `%AppData%\flowboost`; existing legacy settings fields are ignored and are not rewritten until settings are saved.

When an operation fails, flowboost appends a diagnostic entry to `%AppData%\flowboost\log.txt`. Entries contain a UTC timestamp, a fixed event identifier, and (when available) the exception type. The log is append-only; it does not include exception messages or stacks, Copilot responses, prompts, clipboard/selection contents, or authentication tokens. Treat the file as private diagnostic data and review it before sharing.

## Shortcuts

Select text before pressing a shortcut. The default presets are:

| Preset | Shortcut |
| --- | --- |
| TLDR | Ctrl+Alt+Shift+T |
| Explain | Ctrl+Alt+Shift+E |
| Translate | Ctrl+Alt+Shift+R |
| Fix grammar | Ctrl+Alt+Shift+G |

Presets, prompts, and shortcuts can be changed in Settings.

### Optional: Logitech MX Keypad

To trigger a preset with a single key, open Logi Options+ and assign the MX Keypad keys to **Keystroke** actions using the shortcuts above. The same approach works with any programmable keypad or macro key.
