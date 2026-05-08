using System.CommandLine;
using System.Diagnostics;

using PwshHooks.Client; // added for Trace and ConsoleTraceListener

using static PwshHooks.Client.Client;

try
{
  // Cancel any operations on Ctrl+C
  using CancellationTokenSource cts = new();
  Console.CancelKeyPress += (sender, eventArgs) =>
  {
    eventArgs.Cancel = true;
    cts.Cancel();
  };

  //HACK: Needed for validators until https://github.com/dotnet/command-line-api/issues/2766 is resolved
  Options.Initialize();

  RootCommand rootCommand = new("PwshHooks Client - Execute PowerShell scripts via a persistent PowerShell server.")
  {
    Options.File,
    Options.Script,
    Options.WorkingDirectory,
    Options.PipeName,
    Options.Verbose,
    Options.Depth,
    Options.ExeDir,
    Options.ForwardStdin,
    Options.Shutdown
  };

  rootCommand.SetAction(async (parseResult, cancellationToken) =>
  {
    string? stdinContent = null;
    if (parseResult.GetValue(Options.ForwardStdin))
    {
      stdinContent = await Console.In.ReadToEndAsync(cancellationToken);
      if (string.IsNullOrEmpty(stdinContent))
        stdinContent = null;
    }

    // When --forward-stdin is active and --pipe-name was not explicitly provided,
    // derive the pipe name from the session_id in the hook payload so each Claude
    // Code session gets its own isolated server.
    string pipeName = parseResult.GetValue(Options.PipeName)
      ?? throw new ArgumentException("Pipe name cannot be null");

    if (stdinContent is not null && pipeName == $"PwshHooks-{Environment.UserName}"
        && ParseSessionId(stdinContent) is string sessionId)
    {
      pipeName = $"PwshHooks-{sessionId}";
    }

    if (parseResult.GetValue(Options.Shutdown))
    {
      await ShutdownServer(
        pipeName: pipeName,
        verbose: parseResult.GetValue(Options.Verbose),
        cancellationToken: cts.Token
      );
      return;
    }

    string? script = parseResult.GetValue(Options.Script);
    FileInfo? file = parseResult.GetValue(Options.File);
    string resolvedScript = file != null ? $"& (Resolve-Path {file.FullName})" : script!;

    await InvokeScript(
      script: resolvedScript,
      pipeName: pipeName,
      workingDirectory: parseResult.GetValue(Options.WorkingDirectory)?.FullName,
      verbose: parseResult.GetValue(Options.Verbose),
      cts.Token,
      parseResult.GetValue(Options.ExeDir)?.FullName,
      parseResult.GetValue(Options.Depth),
      stdinContent
    );
  });

  ParseResult parseResult = rootCommand.Parse(args);

  // This is our program entrypoint which invokes the script if there are no parsing errors.
  return await parseResult.InvokeAsync();
}
catch (Exception ex)
{
  if (!string.IsNullOrEmpty(ex.Message))
  {
    Console.Error.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
  }
  return (int)ExitCodeMapper.GetExitCode(ex);
}
finally
{
  Trace.Flush();
}
