# Volume to OSC macOS + Windows Prototype

This prototype is a .NET console application that:

1. Registers a **global key hook** for media volume keys
2. Captures hardware volume keys (`Volume Up`, `Volume Down`, `Mute`)
3. Translates those key presses into **OSC (Open Sound Control)** messages over UDP

Supported platforms:
- macOS (Quartz event tap)
- Windows (Win32 low-level keyboard hook)

## Project layout

- `VolumeOscPrototype/Program.cs` - platform hook selection, platform-specific key hooks, OSC packet creation, and runtime wiring.
- `VolumeOscPrototype/VolumeOscPrototype.csproj` - multi-target .NET project (`net8.0-macos`, `net8.0-windows10.0.19041.0`).

## Run

```bash
dotnet run --project VolumeOscPrototype --framework net8.0-macos -- \
  --host=127.0.0.1 \
  --port=9000 \
  --up=/volume/up \
  --down=/volume/down \
  --mute=/volume/mute \
  --step=0.05
```

```bash
dotnet run --project VolumeOscPrototype --framework net8.0-windows10.0.19041.0 -- \
  --host=127.0.0.1 \
  --port=9000 \
  --up=/volume/up \
  --down=/volume/down \
  --mute=/volume/mute \
  --step=0.05
```

### CLI arguments

- `--host` OSC destination host (default `127.0.0.1`)
- `--port` OSC destination UDP port (default `9000`)
- `--up` OSC address for volume up (default `/volume/up`)
- `--down` OSC address for volume down (default `/volume/down`)
- `--mute` OSC address for mute toggle (default `/volume/mute`)
- `--step` float payload for volume steps (default `0.05`)

## Notes

- On macOS, grant Accessibility permission to observe global key events.
- OSC payloads are encoded as single-float messages (`",f"`).
