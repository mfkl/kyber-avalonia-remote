# Kyber Avalonia Client

## Build & Run

Build from WSL (sandbox requires `dangerouslyDisableSandbox` for dotnet due to `/tmp/.dotnet/shm` mutex):

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  TMPDIR=/tmp/claude-1000 HOME=/tmp/claude-1000 DOTNET_CLI_HOME=/tmp/claude-1000 \
  dotnet build KyberAvalonia.csproj -c Debug
```

Both client and server must run on Windows. After building in WSL, copy DLLs to Windows:

```bash
cp kyber-avalonia/bin/Debug/net10.0/KyberAvalonia.dll /mnt/c/temp/kyber-avalonia/bin/Debug/net10.0/
cp kyber-avalonia/bin/Debug/net10.0/KyberAvalonia.pdb /mnt/c/temp/kyber-avalonia/bin/Debug/net10.0/
cp kybersharp/src/KyberSharp/bin/Debug/net10.0/KyberSharp.dll /mnt/c/temp/kyber-avalonia/bin/Debug/net10.0/
```

Launch on Windows with log capture:

```bash
timeout 5 /mnt/c/Windows/System32/cmd.exe /c "cd /d C:\temp\kyber-avalonia\bin\Debug\net10.0 && start /b KyberAvalonia.exe > C:\temp\kyber-avalonia\stdout.log 2> C:\temp\kyber-avalonia\stderr.log"
```

Kill client:

```bash
timeout 5 /mnt/c/Windows/System32/cmd.exe /c "taskkill /IM KyberAvalonia.exe /T /F"
```

## VLC Plugin Path (Critical)

`libvlc.dll` lives at `runtimes/win-x64/native/libvlc.dll` and searches for plugins at `plugins/` **relative to its own directory**. The NuGet package places VLC plugins under `vlc/plugins/` but NOT under `plugins/`. You must copy them:

```bash
cp -r runtimes/win-x64/native/vlc/plugins runtimes/win-x64/native/plugins
```

Without this, VLC cannot open `kymux://` URIs and video will not render (white window, "VLC is unable to open the MRL" errors in logs).

## Log Analysis

Logs are very noisy (QUIC trace-level). Filter with:

```bash
grep "Info\|Warn\|Error" /mnt/c/temp/kyber-avalonia/stdout.log
```

Or for VLC-specific issues:

```bash
grep "vlc\|kymux\|Play video\|Play audio\|unable to open" /mnt/c/temp/kyber-avalonia/stdout.log
```

## Known Issues

- **Exit code 29**: Native crash in `kynput_pipeline_stop` during disconnect cleanup. Wrapped in try/catch to prevent process termination, but the underlying native bug remains.
- **VLC plugins not in NuGet**: The Kyber.Native NuGet packages do not include VLC plugin directories. They must be manually copied from the server distribution or build output.

## Connection Defaults

The client auto-connects on startup with host=localhost, user=demo, password=demo (2 second delay for window init). Software decode is enabled by default.
