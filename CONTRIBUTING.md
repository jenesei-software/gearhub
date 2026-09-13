# Contributing to GearHub

Thanks for wanting to help! GearHub is a small hobby project, and every issue, device report
and pull request matters.

## Ways to contribute

- **Report a bug** — use the [bug report template](https://github.com/jenesei-software/gearhub/issues/new/choose).
- **Request device support** — the device template; the more details, the better.
- **Send a pull request** — fixes, new providers, UI polish, docs.

For Logitech devices, the most useful thing you can attach is the `GearHub.HidProbe`
output (see the README).

## Development setup

Requirements: Windows 10 build 19041+ or Windows 11, and the .NET SDK 10.

```powershell
# Build and run the tests
dotnet build GearHub.slnx -c Debug
dotnet test GearHub.slnx --no-build

# Run the widget
dotnet run --project src/GearHub.App
```

Notes:

- Stop the running widget before rebuilding (`Stop-Process -Name GearHub`) — on Windows the exe
  stays locked and the build fails with a file-in-use error.
- `docs/` contains real screenshots; `tools/GearHub.HidProbe` is the HID++ diagnostic console.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/GearHub.Core` | Domain model, status policy, filtering — no Windows dependencies |
| `src/GearHub.Providers.Windows` | Data sources: XInput, Bluetooth LE (GATT), Logitech HID++ — implement `IGearProvider` here for new hardware |
| `src/GearHub.App` | WPF UI: the screen bar and the tray icon |
| `tests/GearHub.Core.Tests` | Unit tests for the core logic |

## Code guidelines

- Keep comments in English; user-facing strings live in
  `src/GearHub.Core/Localization/Loc.cs` (all six languages — treat them as data).
- Prefer conventional commit prefixes: `feat:`, `fix:`, `docs:`, `chore:`.
- Run the tests before opening a pull request and keep them green.
- Keep pull requests focused: one feature or fix per PR where possible.

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
