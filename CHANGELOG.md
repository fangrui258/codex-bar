# Changelog

All notable changes to CodexBar are documented here.

## [Unreleased]

## [1.3.1] - 2026-11-26

### Fixed

- Fixes Antigravity CLI (`agy`) `statusLine` invocation failing on Windows when the `CodexBar` executable path contains spaces, parentheses, or non-ASCII characters by routing through a quote-free UTF-8 batch shim (`%LOCALAPPDATA%\CodexBar\codexbar-antigravity-statusline.cmd`) with short-path fallback.
- Automatically upgrades existing quoted `--antigravity-statusline` entries in `~/.gemini/antigravity-cli/settings.json` to the batch shim while preserving unrelated custom `statusLine` scripts.

## [1.3.0] - 2026-11-26

### Added

- Adds **Antigravity** quota monitoring alongside Codex with a single-window top tab bar (`CODEX · ANTIGRAVITY`) and dynamic height adaptation for multiple model pools.
- Shows each Antigravity model pool's remaining percentage and local reset schedule, and switches to a `Standby` state when neither `Antigravity` nor `agy` is running.
- Adds a built-in `--antigravity-statusline` bridge mode and tray menu **Setup Antigravity CLI bridge…** action that safely registers `statusLine` in `~/.gemini/antigravity-cli/settings.json` without overwriting existing custom scripts.
- Adds `IUsageProvider` and shared `QuotaLane` / `ProviderSnapshot` abstractions, with per-provider notification state tracking and automatic migration from v1.2.x settings.

### Verification

- Covers Antigravity `statusLine` payload parsing, `reset_in_seconds` fallback, empty quota handling, standby detection, account switching, bridge config safety, and legacy settings migration in the regression suite (14/14 checks passing).

## [1.2.1] - 2026-09-08

### Documentation

- Adds complete English and Simplified Chinese README versions with language navigation.
- Uses the published x64 asset names and links directly to the lightweight and portable downloads.
- Clarifies that prebuilt releases currently target x64 while ARM64 remains available as a source build.
- Describes the exact rate-limit and reset-credit metadata CodexBar reads.

### Fixed

- Restarts the private Codex app-server when an external account switcher replaces `auth.json`, preventing the previous account's cached limits from remaining visible.
- Treats the first reading after an account switch as a notification baseline so one account cannot trigger another account's weekly-reset alert.

## [1.2.0] - 2026-08-27

### Added

- Displays the account-wide 5-hour Codex capacity and reset time alongside the existing weekly limit.
- Covers duration-based window selection, primary/secondary ordering, multi-bucket responses, missing 5-hour windows, and reset-credit parsing with a zero-dependency regression test harness.

### Improved

- Selects the account-wide `codex` bucket from the app-server multi-bucket response when available.
- Keeps weekly usage available when an account does not report a 5-hour window.

## [1.1.1] - 2026-07-31

- Makes the Start with Windows status explicit in the tray menu.
- Adds notification setup guidance for Gmail app passwords and carrier email-to-text addresses.
- Clarifies that the configured refresh interval controls the real rate-limit request frequency.

## [1.1.0] - 2026-07-31

### Added

- Exact local expiration dates and times for every available banked rate-limit reset, ordered nearest first.
- Explicit `Always on top — On/Off` status in the tray menu.
- Optional weekly-capacity reset notifications through SMTP or a prefilled draft in the default mail app.
- A test-alert command and a live/offline connection indicator.

### Improved

- Reuses the supported Codex app-server response for reset-credit details without reading authentication files or requiring a separate login.
- Protects stored SMTP passwords with Windows Data Protection API.
- Validates notification settings, bounds delivery time, and writes settings atomically.
- Falls back across installed Codex executables and distinguishes retryable transport failures from semantic errors.
- Reduces unnecessary settings writes and widget repaints.

## [1.0.0] - 2026-07-22

- Initial public release of the lightweight Windows widget.
- Live weekly Codex capacity and reset-time display.
- Configurable refresh interval, transparency, always-on-top mode, Windows startup, and notification-area support.

[Unreleased]: https://github.com/fangrui258/codex-bar/compare/v1.3.1...HEAD
[1.3.1]: https://github.com/fangrui258/codex-bar/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/fangrui258/codex-bar/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/fangrui258/codex-bar/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/fangrui258/codex-bar/releases/tag/v1.2.0
[1.1.1]: https://github.com/jspann21/codex-bar/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/jspann21/codex-bar/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jspann21/codex-bar/releases/tag/v1.0.0
