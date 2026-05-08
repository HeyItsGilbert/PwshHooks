# Testing & Development

## Building

### Server (no AOT, builds anywhere)

```powershell
dotnet build Source/Server -c Debug
```

Output: `artifacts\bin\Server\debug\PwshHooks.dll`

### Client (AOT — requires MSVC linker)

The client uses .NET 8 AOT compilation and requires the MSVC C++ toolchain. Run from a **Developer PowerShell for VS 2022**:

```powershell
dotnet build Source/Client -c Debug
```

Output: `artifacts\bin\Client\debug\PwshHooksClient.exe`

### Publishing to `Build\`

Both binaries are copied to `Build\` on publish. Kill any running server first (it locks the DLL), then:

```powershell
dotnet publish Source/Server -c Debug
dotnet publish Source/Client -c Release -r win-x64   # must be in Developer PowerShell
```

## Manual End-to-End Test

### 1. Start the server in an interactive window

```powershell
Import-Module D:\PwshHooks\Build\PwshHooks.dll -Force
Start-PwshHooks -PipeName PwshHooks-debug
```

You'll see `Listening for client 1 on PwshHooks-debug` when it's ready.

### 2. Invoke a hook

In a second terminal:

```powershell
'{"hook_event_name":"PreToolUse","session_id":"debug","tool_name":"Bash","tool_input":{"command":"rm -rf /"}}' |
    D:\PwshHooks\artifacts\bin\Client\debug\PwshHooksClient.exe `
        --forward-stdin --pipe-name PwshHooks-debug `
        -c "& D:\ClaudeHooks\Examples\block-rm-rf.ps1"
```

Expected stdout:

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecisionReason":"rm -rf is not allowed. Use Remove-Item -Recurse instead.","permissionDecision":"deny"}}
```

### 3. Test shutdown

```powershell
'{"hook_event_name":"SessionEnd","session_id":"debug"}' |
    D:\PwshHooks\artifacts\bin\Client\debug\PwshHooksClient.exe `
        --forward-stdin --pipe-name PwshHooks-debug --shutdown
```

The server window should print `Shutdown requested by client.` and exit.

## Updating the ClaudeHooks Module

After editing source files under `D:\ClaudeHooks\ClaudeHooks\`, copy the changed files to the installed module so the server picks them up:

```powershell
$dest = (Get-Module ClaudeHooks -ListAvailable | Select-Object -First 1).Path | Split-Path
Copy-Item D:\ClaudeHooks\ClaudeHooks\Public\*.ps1  $dest\Public\  -Force
Copy-Item D:\ClaudeHooks\ClaudeHooks\Private\*.ps1 $dest\Private\ -Force
```

Then restart the server to reload the module into the runspace pool.

## Verbose Diagnostics

Add `--verbose` to the client invocation to see pipe connection details on stderr:

```powershell
... | PwshHooksClient.exe --forward-stdin --verbose -c "..."
```

The server always logs to its own stdout/stderr window — start it interactively to see script execution, item output, and cancellation events.
