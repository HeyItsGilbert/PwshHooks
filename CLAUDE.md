# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Purpose

PwshHooks is a performance-optimized system for repeatedly executing PowerShell scripts with minimal overhead. It consists of:

- **Client** (`Source/Client/`) — .NET 8 AOT-compiled native executable that sends scripts to the server via Windows named pipes
- **Server** (`Source/Server/`) — A PowerShell module exposing `Start-PwshHooks`, a persistent background service that runs scripts via a runspace pool
- **Shared** (`Source/Shared/`) — Length-delimited pipe protocol and shared types

The intended use case is Claude Hooks: instead of spawning a fresh `pwsh.exe` for every hook invocation, hooks connect to the persistent server and reuse warm runspaces.

## Build

```powershell
# Debug build — copies artifacts to Build/
dotnet build Source/Client
dotnet build Source/Server

# Release AOT publish (final distribution binary)
dotnet publish Source/Client -c Release -r win-x64
dotnet publish Source/Server -c Release -r win-x64
```

VSCode tasks (`Build Server`, `Build Client`) do the same. Artifacts land in `Build/` automatically via post-build copy targets in each `.csproj`.

## Test

```powershell
dotnet test
```

Test coverage is minimal — `Test/Client.Test/` exists but is largely empty.

## Architecture

### Communication Protocol

Client and server communicate over a Windows named pipe using a custom length-delimited format: `[4-byte big-endian length][UTF-8 payload]`, implemented in `Source/Shared/StreamString.cs`. No external serialization library is used intentionally — monitoring tools may have strict dependency constraints.

Each message from the server is prefixed with a stream type character:

| Prefix | Meaning |
|--------|---------|
| `O:` | Output (JSON-serialized) |
| `E:` | Error record |
| `W:` | Warning |
| `V:` | Verbose |
| `D:` | Debug |
| `I:` | Information |

Special control messages: `<<CANCEL>>`, `<<CANCELLED>>`, `<<END>>`.

### Client (`Source/Client/`)

- `Main.cs` — `System.CommandLine` entrypoint; defines CLI options (`--script`, `--depth`, `--pipe-name`, `--timeout`, etc.) and wires `SetAction` to the execution handler
- `Client.cs` — Named pipe connection logic; auto-starts the server process if the pipe doesn't exist; base64-encodes the script for transmission; streams typed response lines to stdout/stderr

### Server (`Source/Server/`)

- `Server.cs` — `Start-PwshHooks` cmdlet; owns the `RunspacePool` (sized `ProcessorCount * 2`); loops accepting pipe connections and dispatches each to a Task; serializes all pipeline output to JSON with configurable depth

### Key Design Decisions

1. **AOT client** — `PublishAot=true` with `InvariantGlobalization`, `StackTraceSupport=false` for minimal binary size and cold-start time
2. **Runspace pooling** — Amortizes `pwsh.exe` startup across invocations; the pool stays alive between script runs
3. **Shallow output convention** — JSON serialization can deadlock on deep object graphs; scripts should return `PSCustomObject` or `Select-Object`-projected values
4. **Environment variables** — Not forwarded from client to server; pass values as script parameters instead
5. **Stream filtering** — Only Output and Error streams are actively captured; Warning/Verbose/Debug/Progress are discarded at the pipeline level

### Named Pipe Resolution

- Windows: `\\.\pipe\{pipeName}`
- Unix: `/tmp/CoreFxPipe_{pipeName}`

Pipe existence is checked via P/Invoke (`GetNamedPipeServerProcessId`) on Windows before attempting connection.

## Known Gaps / Open TODOs

- No option to fail-fast if server is not already running (always auto-starts)
- Connection timeout is hardcoded (not yet CLI-configurable)
- No option to shut down the server from the client side
- Test suite is a placeholder
