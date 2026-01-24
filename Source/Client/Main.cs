using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

using PowerServe.Client; // added for Trace and ConsoleTraceListener

using static PowerServe.Client.Client;

try
{
  // Write all trace output to console
  Trace.Listeners.Add(new ConsoleTraceListener(true));

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

  ParseResult options = rootCommand.Parse(args);

  if (options.Errors.Count > 0)
  {
    foreach (ParseError error in options.Errors)
    {
      string subject = error.SymbolResult?.ToString() ?? "unknown";
      Console.Error.WriteLine($"Error while parsing '{subject}': {error.Message}");
    }
    throw new ArgumentException(string.Empty);
  }

  string? script = options.GetValue(Options.Script);
  FileInfo? file = options.GetValue(Options.File);
  string resolvedScript = file != null ? $"& (Resolve-Path {file.FullName})" : script!;

  await InvokeScript(
    script: resolvedScript,
    pipeName: options.GetValue(Options.PipeName) ?? throw new ArgumentException("Pipe name cannot be null"),
    workingDirectory: options.GetValue(Options.WorkingDirectory)?.FullName,
    verbose: options.GetValue(Options.Verbose),
    cts.Token,
    options.GetValue(Options.ExeDir)?.FullName,
    options.GetValue(Options.Depth)
  );
}
catch (Exception ex)
{
  if (!string.IsNullOrEmpty(ex.Message))
  {
    Console.Error.WriteLine($"ERROR: {ex.Message}");
  }
  return (int)ExitCodeMapper.GetExitCode(ex);
}
finally
{
  Trace.Flush();
}

// Default result if no exceptions occured
return (int)ExitCode.Success;