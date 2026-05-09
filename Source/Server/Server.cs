using System.IO.Pipes;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

using Microsoft.PowerShell.Commands;

using PwshHooks.Shared;

namespace PwshHooks;

public class PowerShellTarget
{
  private readonly InitialSessionState initialSessionState = InitialSessionState.CreateDefault();
  private readonly RunspacePool runspacePool;

  public PowerShellTarget() : this(null) { }
  public PowerShellTarget(int? maxRunspaces)
  {
    runspacePool = RunspaceFactory.CreateRunspacePool(initialSessionState);
    runspacePool.SetMaxRunspaces(maxRunspaces ?? Environment.ProcessorCount * 2);
    runspacePool.Open();
  }

  public async Task RunScriptJsonAsync(string script, Func<string, Task> writeClient, CancellationToken cancellationToken, int depth = 5)
  {
    _ = Console.Error.WriteLineAsync($"Running script: {script}");
    using PowerShell ps = PowerShell.Create();
    ps.RunspacePool = runspacePool;

    using PSDataCollection<PSObject> outputCollection = new() { BlockingEnumerator = true };

    cancellationToken.Register(() =>
    {
      _ = Console.Error.WriteLineAsync($"Cancellation requested. Stopping script: {script}");
      // This generates a PipelineStoppedException that must be handled
      ps.Stop();
      _ = Console.Error.WriteLineAsync($"Script Stopped");
    });

    if (cancellationToken.IsCancellationRequested)
    {
      _ = Console.Error.WriteLineAsync($"Cancellation requested. Stopping script: {script}");
      return;
    }
    PSInvocationSettings settings = new()
    {
      RemoteStreamOptions = RemoteStreamOptions.AddInvocationInfoToErrorRecord
    };

    ps.AddScript(script);
    // Redirect all streams to output (e.g. Error, Warning) so it is captured by the collection
    ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.All, PipelineResultTypes.Output);

    Task<PSDataCollection<PSObject>> invokeTask = ps.InvokeAsync<PSObject, PSObject>(
      input: null,
      outputCollection,
      settings,
      callback: null,
      state: null
    );

    JsonObject.ConvertToJsonContext context = new(depth, true, true);

    // Buffer O: (script output) items so the entire payload can be validated against the
    // Claude Hooks output schema before being forwarded. Other streams (E/W/V/D/I) pass
    // through live so progress and errors are not delayed.
    List<string> outputPayloads = [];

    Task outputTask = Task.Run(async () =>
    {
      foreach (var item in outputCollection)
      {
        switch (item.BaseObject)
        {
          case DebugRecord dr:
            await writeClient($"D:{dr}");
            break;
          case VerboseRecord vr:
            await writeClient($"V:{vr}");
            break;
          case InformationRecord ir:
            await writeClient($"I:{ir}");
            break;
          case WarningRecord wr:
            await writeClient($"W:{wr}");
            break;
          case ErrorRecord er:
            await writeClient($"E:{er}");
            break;
          case string s:
            // Whitespace-only string output is a Claude Hooks no-op; don't let it count
            // toward the multi-output error or get forwarded as a non-JSON O: payload.
            if (!string.IsNullOrWhiteSpace(s))
            {
              outputPayloads.Add(s);
            }
            break;
          default:
            outputPayloads.Add(JsonObject.ConvertToJson(item, in context));
            break;
        }
      }
    });

    try
    {
      _ = await invokeTask;
    }
    catch (RuntimeException ex)
    {
      _ = Console.Error.WriteLineAsync($"Script Error: {ex}");
      await writeClient($"SCRIPT ERROR: {ex.InnerException} {ex.InnerException?.StackTrace}");
    }
    catch (Exception ex)
    {
      _ = Console.Error.WriteLineAsync($"Server Error: {ex}");
      await writeClient($"SERVER ERROR: {ex}");
    }
    // PowerShell doesn't auto-close the collection, we must do it manually. This will unblock the outputTask after it finishes all the items in the collection.
    outputCollection.Complete();
    await outputTask;

    if (outputPayloads.Count > 1)
    {
      await writeClient($"E:hook emitted {outputPayloads.Count} output objects, expected exactly one JSON object matching the Claude Hooks output schema");
      return;
    }

    if (outputPayloads.Count == 1)
    {
      string payload = outputPayloads[0];
      string? error = HookOutputValidator.Validate(payload);
      if (error is not null)
      {
        await writeClient($"E:invalid hook output: {error}");
        return;
      }
      await writeClient($"O:{payload}");
    }
  }
}

public class Server
{
  private readonly PowerShellTarget Target = new();
  private readonly CancellationTokenSource CancellationTokenSource = new();
  private CancellationToken CancellationToken => CancellationTokenSource.Token;
  private int ClientSessionId = 1;

  public async Task StartAsync(string pipeName) => await StartAsync(pipeName, CancellationToken);
  public async Task StartAsync(string pipeName, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(pipeName))
    {
      pipeName = "PwshHooks-" + Environment.UserName;
    }
    Dictionary<Type, int> retryExceptions = [];
    NamedPipeServerStream? pipeServer = CreatePipe(pipeName);

    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        try
        {
          // This should be the first await in the method so that the pipe server is running before we return to our caller.
          _ = Console.Out.WriteLineAsync($"Listening for client {ClientSessionId} on {pipeName}");
          await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          break;
        }
        catch (IOException ex)
        {
          retryExceptions.TryGetValue(ex.GetType(), out int retryThisExceptionType);
          retryExceptions[ex.GetType()] = retryThisExceptionType + 1;

          // The client has disconnected prematurely before WaitForConnectionAsync could pick it up.
          // Ignore that and wait for the next connection unless cancellation is requested.
          if (cancellationToken.IsCancellationRequested)
          {
            break;
          }

          await pipeServer.DisposeAsync().ConfigureAwait(false);
          pipeServer = CreatePipe(pipeName);
          continue;
        }

        // A client has connected. Open a stream to it (and possibly start listening for the next client)
        // unless cancellation is requested.
        if (!cancellationToken.IsCancellationRequested)
        {
          _ = Console.Out.WriteLineAsync($"Client {ClientSessionId} connected to {pipeName}");
          ClientSessionId++;


          // We invoke the callback in a fire-and-forget fashion as documented. It handles its own exceptions.
          _ = HandleClientConnectionAsync(pipeServer);
          // Start listening for the next client.
          pipeServer = CreatePipe(pipeName);
        }
      }
    }
    finally
    {
      if (pipeServer is not null)
      {
        await pipeServer.DisposeAsync().ConfigureAwait(false);
      }
    }

    static NamedPipeServerStream CreatePipe(string pipeName)
    {
      return new(
        pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
      );
    }
  }

  async Task HandleClientConnectionAsync(NamedPipeServerStream serverStream)
  {
    StreamString reader = new(serverStream);
    StreamString writer = new(serverStream);
    string inFromClient = await reader.ReadAsync();

    _ = Console.Out.WriteLineAsync("IN: " + inFromClient);

    if (inFromClient == "<<SHUTDOWN>>")
    {
      _ = Console.Out.WriteLineAsync("Shutdown requested by client.");
      await writer.WriteAsync("<<END>>");
      CancellationTokenSource.Cancel();
      return;
    }

    // Protocol variants (space-delimited, script is always last):
    //   "{script}"                          — no depth, no stdin
    //   "{depth} {script}"                  — depth prefix
    //   "{depth} {stdinBase64} {script}"    — depth + forwarded stdin
    var parts = inFromClient.Split(' ', 3);
    int depth = 5;
    string? stdinContent = null;
    string script;

    if (parts.Length == 3 && int.TryParse(parts[0], out depth))
    {
      stdinContent = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
      script = parts[2];
    }
    else if (parts.Length >= 2 && int.TryParse(parts[0], out depth))
    {
      script = parts[1];
    }
    else
    {
      depth = 5;
      script = inFromClient;
    }

    if (stdinContent != null)
    {
      // Inject into global scope so module-defined functions (e.g. Read-ClaudeHookInput) can
      // read it. Each runspace has its own global scope, so there is no cross-contamination
      // between concurrent invocations. Base64 avoids any PS string-escaping concerns.
      string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(stdinContent));
      script = $"$global:ClaudeHookInput = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{b64}'))\n{script}";
    }

    using CancellationTokenSource runScriptCts = new();
    try
    {

      Task cancellationReadTask = Task.Run(() =>
      {
        Console.Error.WriteLine("Waiting for Cancellation.");
        // This should block on the client until a new line is received or the stream is closed.
        try
        {
          if (reader.Read() == "<<CANCEL>>")
          {
            Console.Error.WriteLine("Cancel message received by client. Cancelling script.");
            runScriptCts.Cancel();
          }
          else
          {
            Console.Error.WriteLine("No cancel message received. Continuing script.");
          }
        }
        catch (EndOfStreamException)
        {
          Console.Error.WriteLine("Stream completed with no cancellation. This is normal.");
        }
      });

      await Target.RunScriptJsonAsync(
        script,
        async outLine => await writer.WriteAsync(outLine),
        runScriptCts.Token,
        depth
      );
    }
    catch (PipelineStoppedException)
    {
      if (!runScriptCts.IsCancellationRequested)
      {
        Console.Error.WriteLine($"SERVER ERROR: Script unexpectedly stopped");
      }
      Console.Error.WriteLine($"Script Succssfully Cancelled");
      await writer.WriteAsync("<<CANCELLED>>");
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Error running script: {ex}");
      if (serverStream.CanWrite)
      {
        await writer.WriteAsync($"SERVER ERROR: {ex}");
      }
    }

    // Signal the end of the response.
    _ = Console.Out.WriteLineAsync("End of script ");
    await writer.WriteAsync("<<END>>");
  }
}