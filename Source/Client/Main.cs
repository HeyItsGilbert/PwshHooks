using System.CommandLine;
using System.Diagnostics;

using PowerServe.Client; // added for Trace and ConsoleTraceListener

using static PowerServe.Client.Client;

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

  RootCommand rootCommand = new("PowerServe Client - Execute PowerShell scripts via a persistent PowerShell server.")
  {
    Options.File,
    Options.Script,
    Options.WorkingDirectory,
    Options.PipeName,
    Options.Verbose,
    Options.Depth,
    Options.ExeDir
  };

  rootCommand.SetAction(async (parseResult, cancellationToken) =>
  {
    string? script = parseResult.GetValue(Options.Script);
    FileInfo? file = parseResult.GetValue(Options.File);
    string resolvedScript = file != null ? $"& (Resolve-Path {file.FullName})" : script!;

    await InvokeScript(
      script: resolvedScript,
      pipeName: parseResult.GetValue(Options.PipeName) ?? throw new ArgumentException("Pipe name cannot be null"),
      workingDirectory: parseResult.GetValue(Options.WorkingDirectory)?.FullName,
      verbose: parseResult.GetValue(Options.Verbose),
      cts.Token,
      parseResult.GetValue(Options.ExeDir)?.FullName,
      parseResult.GetValue(Options.Depth)
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
