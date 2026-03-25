# Kyber Avalonia Client

## Build & Run (macOS)

Requires .NET 10 SDK. Install via homebrew:
```bash
brew install dotnet-sdk
```

Build:
```bash
dotnet build -c Debug
```

### Native Libraries Setup (macOS)

The native libraries must be copied to the app's native folder before running:

```bash
# Copy libkyclient
cp /Users/martin/code/kyber/kyber-desktop/kysdk/kyctl/target/aarch64-apple-darwin/release/libkyclient.dylib \
   bin/Debug/net10.0/runtimes/osx/native/

# Copy VLC libraries (libkyclient depends on these via @rpath)
cp /Users/martin/code/kyber/kyber-desktop/kysdk/kyctl/rootfs-arm64-apple-darwin/lib/libvlc*.dylib \
   bin/Debug/net10.0/runtimes/osx/native/
```

**CRITICAL: Remove duplicate kynput libraries to prevent crashes**

The build may copy kynput libraries to `bin/Debug/net10.0/` alongside the executable. These MUST be removed to prevent dual-library loading crashes (SIGSEGV in `PacketQueue::push`):

```bash
rm -f bin/Debug/net10.0/libkynput*.dylib
rm -f bin/Debug/net10.0/runtimes/osx/native/libkynput*.dylib
```

The kynput library is already loaded by libkyclient from its hardcoded rootfs path. Having a second copy causes mutex poisoning and memory corruption.

### Run (macOS)

Due to `@rpath` in libkyclient.dylib, must set DYLD_LIBRARY_PATH to include both the native folder and the rootfs:

```bash
cd bin/Debug/net10.0/runtimes/osx/native
DYLD_LIBRARY_PATH=$(pwd):/Users/martin/code/kyber/kyber-desktop/kysdk/kyctl/rootfs-arm64-apple-darwin/lib ../../../KyberAvaloniaRemoteClient
```

Or as a one-liner:
```bash
DYLD_LIBRARY_PATH=$PWD/bin/Debug/net10.0/runtimes/osx/native:/Users/martin/code/kyber/kyber-desktop/kysdk/kyctl/rootfs-arm64-apple-darwin/lib ./bin/Debug/net10.0/KyberAvaloniaRemoteClient
```

### Debug library loading (macOS)
```bash
DYLD_PRINT_LIBRARIES=1 DYLD_LIBRARY_PATH=... ./KyberAvaloniaRemoteClient
```

---

## Build & Run (Windows/WSL)

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

## Fixed Issues

- **macOS input crash (FIXED)**: Previously, clicking in the video area caused SIGSEGV crashes. Root causes:
  1. **Double-free bug**: In `kynput/src/capi/mod.rs`, `kynput_consumer_consume()` was calling `Box::from_raw(pkt)` which took ownership and freed the packet, while the C# caller also freed it. Fixed by cloning the packet instead: `let pkt = (*pkt).clone();`
  2. **Dual library loading**: The build copied libkynput.dylib to both `bin/Debug/net10.0/` and potentially `runtimes/osx/native/`, while libkyclient.dylib loads libkynput.0.1.0.dylib from a hardcoded rootfs path. Two library instances with separate global state caused mutex poisoning. Fixed by removing duplicate libkynput libraries from the app directories.

## Connection Defaults

The client auto-connects on startup with host=localhost, user=demo, password=demo (2 second delay for window init). Software decode is enabled by default.
