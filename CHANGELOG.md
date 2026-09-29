# Changelog

All notable changes to flowboost are documented in this file.

This changelog follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and flowboost uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] - 2026-09-29

### Added

- Result popup shows elapsed waiting time in the title-bar badge after 15 seconds, and a **Stop** button to abort a pending Copilot reply.
- When a model is unavailable (for example, a 503 outage), the popup shows a plain-language message and an inline **Try another model** panel: pick a model, retry the same request, and optionally make it the default.
- Expired sign-in errors in the popup include an **Open Settings** shortcut.

### Changed

- Runtime error text is classified and shortened before display; request identifiers are removed.
- Enabled ReadyToRun compilation for the published executable.

## [0.1.1] - 2026-09-28

### Added

- Update check: at startup (and via Settings > About > **Check for updates**) flowboost queries the latest GitHub Release. When a newer version exists, a dialog offers **Open GitHub**, **Remind me later**, or **Skip this version**. Nothing is downloaded or installed automatically.
- Saved sign-in validation: at startup and when the Copilot runtime reports a missing token, the saved GitHub token is checked against GitHub. A revoked token shows "Your GitHub sign-in has expired. Sign in again." instead of a runtime error; the saved token is kept until a new sign-in replaces it.

### Changed

- README clarifies that flowboost works with any keyboard shortcut; the Logitech MX Keypad is optional. Build output location (`publish\flowboost.exe`) is documented.
- The app version is read from assembly metadata in one place and used for the About card and outgoing `User-Agent` headers.

## [0.1.0] - 2026-09-24

### Added

- Windows notification-area tray application for working with GitHub Copilot prompts. Only one instance runs per Windows session.
- Bundled application icon, shown for the executable, the tray, and the Settings window.
- Configurable presets and keyboard shortcuts for sending the selected text with a prompt.
- Result popup that opens as soon as text is captured, shows request status in its title bar, and streams the Copilot response using the GitHub Copilot SDK.
- Chat in the result popup: follow-up comments or questions continue the same Copilot conversation.
- Movable, resizable result popup sized to the selected text, with a Copy action for the latest reply.
- GitHub OAuth device-flow sign-in using the app's embedded public client ID, with tokens protected by Windows DPAPI and stored under `%AppData%\flowboost`. Sign-in can be repeated to replace a saved sign-in without deleting it first.
- Recovery when Copilot rejects a saved sign-in: the model list is retried once with a fresh Copilot runtime, then Settings shows a clear message.
- Settings window with account, model, prompt, general options, app version, and bundled release notes.
- Append-only diagnostic log at `%AppData%\flowboost\log.txt` containing fixed event identifiers and exception types only.
- `build.ps1` script that publishes `publish\flowboost.exe` from the repository root.
