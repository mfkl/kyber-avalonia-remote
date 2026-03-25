# Kyber Avalonia Remote

A cross-platform remote desktop solution built with [Avalonia UI](https://avaloniaui.net/). Stream your desktop with low-latency video and full keyboard/mouse input forwarding.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              CLIENT MACHINE                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │                   kyber-avalonia-remote-client                       │   │
│  │                        (Avalonia UI)                                 │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                   │   │
│  │  │ Video Panel │  │ Input Panel │  │  Log Panel  │                   │   │
│  │  │  (VLC)      │  │ (kbd/mouse) │  │             │                   │   │
│  │  └──────┬──────┘  └──────┬──────┘  └─────────────┘                   │   │
│  └─────────┴────────────────┴───────────────────────────────────────────┘   │
│            │                │                                               │
│  ┌─────────┬────────────────┬───────────────────────────────────────────┐   │
│  │         ▼                ▼                                           │   │
│  │                         KyberSharp                                   │   │
│  │              (C# P/Invoke bindings for Kyber SDK)                    │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                   │   │
│  │  │ KyberClient │  │InputPipeline│  │ VideoLayout │                   │   │
│  │  └──────┬──────┘  └──────┬──────┘  └─────────────┘                   │   │
│  └─────────┴────────────────┴───────────────────────────────────────────┘   │
│            │                │                                               │
│  ┌─────────┬────────────────┬───────────────────────────────────────────┐   │
│  │         ▼                ▼                                           │   │
│  │                    Native Kyber SDK (Rust)                           │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                   │   │
│  │  │ libkyclient │  │  libkynput  │  │   libvlc    │                   │   │
│  │  │ (QUIC conn) │  │ (input fwd) │  │ (video dec) │                   │   │
│  │  └──────┬──────┘  └──────┬──────┘  └─────────────┘                   │   │
│  └─────────┴────────────────┴───────────────────────────────────────────┘   │
│            │                │                                               │
└────────────┴────────────────┴───────────────────────────────────────────────┘
             │                │
             │   QUIC/UDP     │   QUIC/UDP
             │   (video)      │   (input)
             │                │
┌────────────┬────────────────┬───────────────────────────────────────────────┐
│            ▼                ▼                                               │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │                       kycontroller                                   │   │
│  │                  (Native Kyber Server)                               │   │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                   │   │
│  │  │ Screen Cap  │  │Input Inject │  │ Video Encode│                   │   │
│  │  └─────────────┘  └─────────────┘  └─────────────┘                   │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│            ▲                                                                │
│  ┌─────────┴────────────────────────────────────────────────────────────┐   │
│  │                   kyber-avalonia-remote-server                       │   │
│  │                        (Avalonia UI)                                 │   │
│  │         Process manager & configuration UI for kycontroller          │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
├─────────────────────────────────────────────────────────────────────────────┤
│                              SERVER MACHINE                                 │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow

1. **Connection**: Client establishes QUIC connection to server via libkyclient
2. **Video**: Server captures screen, encodes (H.264/HEVC), streams via kymux:// protocol to VLC
3. **Input**: Client captures keyboard/mouse, sends via libkynput, server injects into OS

## Components

- **kyber-avalonia-remote-client** - Remote desktop client that connects to a Kyber server, renders video via VLC, and forwards input
- **kyber-avalonia-remote-server** - Server UI that wraps the kycontroller process for easy hosting

## Features

- Low-latency video streaming via QUIC protocol
- Hardware and software video encoding/decoding
- Full keyboard and mouse input forwarding
- Auto-reconnection on connection drops
- Cross-platform: Windows and macOS

## Requirements

- .NET 10 SDK
- Native Kyber libraries (libkyclient, VLC)
- KyberSharp bindings (referenced as project)

## Project Structure

```
kyber-avalonia-remote/
├── kyber-avalonia-remote-client/   # Client application
├── kyber-avalonia-remote-server/   # Server application
├── kybersharp/                     # C# bindings for native Kyber SDK
└── kyber-nuget/                    # NuGet package sources
```

## Building

```bash
# Client
cd kyber-avalonia-remote-client
dotnet build -c Debug

# Server
cd kyber-avalonia-remote-server
dotnet build -c Debug
```

## Usage

### Server

1. Launch the server application
2. Set the path to `kycontroller` (auto-detected if in standard locations)
3. Configure port and password
4. Click Start

The server displays the LAN IP and connection info for clients.

### Client

1. Launch the client application
2. Enter the server host, username, and password
3. Click Connect
4. Click the video area to capture keyboard/mouse input

## Platform Notes

See `kyber-avalonia-remote-client/CLAUDE.md` for detailed platform-specific build and run instructions, including native library setup for macOS and Windows.

## License

AGPL-3.0
