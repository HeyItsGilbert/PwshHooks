using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using PwshHooks.Shared;

namespace PwshHooks.Client;

static class Client
{
  /// <summary>
  /// An invocation of the client. We connect, send the script, and receive the response in JSONLines format.
  /// </summary>
  public static async Task InvokeScript(string script, string pipeName, string? workingDirectory, bool verbose, CancellationToken cancellationToken, string? exeDir, int depth, string? stdinContent = null)
  {
    if (verbose)
    {
      // Log all tracing to stderr.
      Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
    }

    // Check if the named pipe client exists

    using var pipeClient = new NamedPipeClientStream(
      ".",
      pipeName,
      PipeDirection.InOut,
      PipeOptions.Asynchronous
    );

    // TODO: Implement a command line option to end server at the end of invocation

    // TODO: Implement a command line option to not automatically start a server but instead fail if not connected
    if (!NamedPipeExists(pipeName))
    {
      Trace.TraceInformation($"PwshHooks is not listening on pipe {pipeName}. Spawning new pwsh.exe PwshHooks listener...");

      if (string.IsNullOrWhiteSpace(exeDir))
      {
        exeDir = Environment.GetEnvironmentVariable("POWERSERVE_EXE_DIR")
        ?? Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
        ?? Environment.CurrentDirectory;
      }

      ProcessStartInfo startInfo = new()
      {
        FileName = "pwsh",
        CreateNoWindow = true,
        UseShellExecute = false,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = workingDirectory ?? exeDir,
        ArgumentList =
        {
          "-NoProfile",
          "-NoExit",
          "-NonInteractive",
          "-Command", $"Import-Module $(Join-Path (Resolve-Path '{exeDir}') 'PwshHooks.dll');Start-PwshHooks -PipeName {pipeName}"
        }
      };

      Process process = new()
      {
        StartInfo = startInfo
      };

      try
      {
        process.Start();
      }
      catch (OperationCanceledException)
      {
        throw new OperationCanceledException("PwshHooks startup was cancelled.");
      }

      Trace.TraceInformation($"Started PwshHooks listener with PID {process.Id}.");
    }

    try
    {
      // TODO: Make timeout configurable via command line option
      await pipeClient.ConnectAsync(3000, cancellationToken);
    }
    catch (OperationCanceledException)
    {
      throw new OperationCanceledException("Connection to PwshHooks was canceled");
    }
    catch (TimeoutException)
    {
      throw new TimeoutException($"PwshHooks connection failed to pipe {pipeName}.");
    }

    if (!pipeClient.IsConnected)
    {
      throw new InvalidOperationException($"Failed to connect to PwshHooks on pipe {pipeName}.");
    }

    Trace.TraceInformation($"Connected to PwshHooks on Pipe {pipeName}.");

    Trace.TraceInformation($"Script contents: {script}");

    StreamString reader = new(pipeClient);
    StreamString writer = new(pipeClient);

    // Register a callback to send <<CANCEL>> when cancellation is requested
    cancellationToken.Register(() =>
      {
        Trace.TraceWarning("Cancellation requested. Sending <<CANCEL>> to server.");
        writer.Write("<<CANCEL>>");
      });

    // Send the script to the server. When stdin content is present, encode it as base64 and
    // include it as the second field so the server can inject $ClaudeHookInput into the script.
    // Protocol: "{depth} {script}" or "{depth} {stdinBase64} {script}"
    string payload = stdinContent != null
      ? $"{depth} {Convert.ToBase64String(Encoding.UTF8.GetBytes(stdinContent))} {script}"
      : $"{depth} {script}";
    Trace.TraceInformation($"Writing to Server: {payload}");
    int writtenBytes = await writer.WriteAsync(payload, cancellationToken);
    Trace.TraceInformation($"Wrote {writtenBytes} bytes to server.");

    string? response;
    while ((response = reader.Read()) != null)
    {
      if (response == "<<END>>")
      {
        Trace.TraceInformation("Received <<END>>. Terminating normally.");
        break;
      }

      if (response == "<<CANCELLED>>")
      {
        Trace.TraceInformation("Script Cancelled Successfully.");
        continue;
      }

      string[] responseParts = response.Split(':', 2);
      if (responseParts.Length != 2 && responseParts[0].Length != 1)
      {
        throw new InvalidOperationException($"Invalid message format from server: {response}");
      }

      string type = responseParts[0];
      string message = responseParts[1];

      switch (type)
      {
        case "O":
          Console.WriteLine(message);
          continue;
        case "D":
          Console.Error.WriteLine($"DEBUG: {message}");
          continue;
        case "V":
          Console.Error.WriteLine($"VERBOSE: {message}");
          continue;
        case "I":
          Console.Error.WriteLine($"INFO: {message}");
          continue;
        case "W":
          Console.Error.WriteLine($"WARNING: {message}");
          continue;
        case "E":
          Console.Error.WriteLine($"ERROR: {message}");
          continue;
        default:
          throw new InvalidOperationException($"Invalid message type from server: {type}");
      }
    }

    if (response != "<<END>>")
    {
      throw new InvalidOperationException("Connection closed unexpectedly before receiving <<END>>.");
    }
  }

  /// <summary>
  /// Check if a named pipe exists in a cross-platform manner. Reimplementation of a private .NET core method.
  /// </summary>
  public static bool NamedPipeExists(string pipeName)
  {
    if (Path.IsPathFullyQualified(pipeName))
    {
      string directory = Path.GetDirectoryName(pipeName) ?? throw new InvalidOperationException("IsPathFullyQualified was true but GetDirectoryName returned null. This is a bug and should not happen.");
      string fileName = Path.GetFileName(pipeName);
      return Directory.GetFiles(directory, fileName).Length > 0;
    }

    return Directory.GetFiles(
      OperatingSystem.IsWindows() ? @"\\.\pipe\" : Path.GetTempPath(),
      OperatingSystem.IsWindows() ? pipeName : $"CoreFxPipe_{pipeName}"
    ).Length > 0;
  }

  /// <summary>
  /// Connects to a running PwshHooks server and sends a shutdown signal, then waits for
  /// acknowledgement. If no server is listening on the pipe, returns without error.
  /// </summary>
  public static async Task ShutdownServer(string pipeName, bool verbose, CancellationToken cancellationToken)
  {
    if (verbose)
      Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));

    if (!NamedPipeExists(pipeName))
    {
      Trace.TraceInformation($"No PwshHooks server on pipe {pipeName}. Nothing to shut down.");
      return;
    }

    using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    try
    {
      await pipeClient.ConnectAsync(3000, cancellationToken);
    }
    catch (TimeoutException)
    {
      throw new TimeoutException($"PwshHooks connection timed out on pipe {pipeName}.");
    }

    StreamString writer = new(pipeClient);
    StreamString reader = new(pipeClient);

    await writer.WriteAsync("<<SHUTDOWN>>", cancellationToken);

    // Drain until <<END>> so the server finishes its cleanup before we exit.
    string? response;
    while ((response = reader.Read()) is not null && response != "<<END>>") { }

    Trace.TraceInformation($"PwshHooks server on {pipeName} shut down.");
  }

  /// <summary>
  /// Extracts the session_id field from a Claude Code hook event JSON payload.
  /// Returns null if the field is absent or the input is not valid JSON.
  /// Uses JsonDocument (no reflection) so it is safe in AOT builds.
  /// </summary>
  public static string? ParseSessionId(string json)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      if (doc.RootElement.TryGetProperty("session_id", out JsonElement el))
        return el.GetString();
    }
    catch { }
    return null;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool GetNamedPipeServerProcessId(IntPtr hPipe, out int ClientProcessId);
}
