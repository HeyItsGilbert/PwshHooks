# PwshHooks

A performance-optimized runtime for PowerShell-based [Claude Code hooks](https://code.claude.com/docs/en/hooks). Instead of spawning a new `pwsh.exe` process for every hook invocation, PwshHooks keeps a persistent PowerShell server alive for the duration of a Claude Code session and routes hook calls through it via named pipes.

## How It Works

`PwshHooksClient.exe` is a .NET 8 AOT-compiled native executable. On each hook invocation it:

1. Reads the Claude Code event JSON from stdin
2. Derives the pipe name from the `session_id` in that payload
3. Connects to the session's PwshHooks server (starts one if not running)
4. Forwards the event JSON and the hook script to the server
5. Streams the JSON response back to stdout for Claude Code to consume

The server is a persistent `pwsh.exe` process hosting a runspace pool. It stays alive across all hook calls for the session and is shut down by the `SessionEnd` hook.

## Requirements

- PowerShell 7+
- [ClaudeHooks](https://github.com/justingrote/ClaudeHooks) module installed (`Install-Module ClaudeHooks`)

## Setup

### 1. Install

Download the latest release and extract `PwshHooksClient.exe` and `PwshHooks.dll` to a permanent location, e.g. `~/.claude/PwshHooks/`.

### 2. Write a hook script

Hook scripts import `ClaudeHooks` and use `Read-ClaudeHookInput` to access the event. The event JSON is injected automatically — no stdin plumbing needed.

```powershell
# ~/.claude/hooks/block-rm-rf.ps1
Import-Module ClaudeHooks

$hook = Read-ClaudeHookInput -Quiet
if (-not $hook) { exit 0 }

$base = Get-ClaudeBashBaseCommand $hook.tool_input.command
if ($base -eq 'rm' -and $hook.tool_input.command -match '-rf?') {
    Write-ClaudeHookDeny -Reason 'rm -rf is not allowed.'
}
exit 0
```

### 3. Register hooks in `~/.claude/settings.json`

Replace `<install-dir>` with the path where you extracted the binaries.

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "<install-dir>/PwshHooksClient.exe --forward-stdin -c \"& ~/.claude/hooks/block-rm-rf.ps1\""
          }
        ]
      }
    ],
    "SessionEnd": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "<install-dir>/PwshHooksClient.exe --forward-stdin --shutdown"
          }
        ]
      }
    ]
  }
}
```

The `SessionEnd` entry is important — it shuts down the session's server process cleanly when Claude Code exits.

You can also use `Add-ClaudeHookConfig` from the ClaudeHooks module to register hooks without editing JSON by hand.

## CLI Reference

```
PwshHooksClient.exe [options]

Options:
  -c, --script <script>            PowerShell script string to execute
  -f, --file <file>                Path to a PowerShell script file to execute
  -i, --forward-stdin              Read stdin and inject it as $global:ClaudeHookInput
  -x, --shutdown                   Shut down the session server and exit
  -p, --pipe-name <name>           Override the pipe name (default: PwshHooks-<username>,
                                   or PwshHooks-<session_id> when --forward-stdin is active)
  -d, --depth <n>                  JSON serialization depth (default: 2)
  -w, --working-directory <dir>    Working directory for spawning the server
  -e, --exeDir <dir>               Directory containing PwshHooks.dll
  -v, --verbose                    Log diagnostic output to stderr
```

## Development

See [TESTING.md](TESTING.md) for build instructions, manual end-to-end test commands, and how to update the ClaudeHooks module during development.

## Shoutout

This project is built on top of [PowerServe](https://github.com/justingrote/PowerServe) by [Justin Grote](https://github.com/justingrote). The persistent server architecture, runspace pooling, named pipe protocol, and AOT client are all his work — PwshHooks would not exist without it.

## Limitations

- Only the Output and Error streams are returned to Claude Code. Warning, Verbose, Debug, and Progress output is discarded.
- JSON serialization can hang on deeply nested objects. Return `PSCustomObject` or `Select-Object`-projected values; keep the default `--depth 2`.
- Environment variables are not forwarded from the client to the server process. Pass values as script parameters instead.
