using System.Management.Automation;

namespace PwshHooks;

[Cmdlet(VerbsLifecycle.Start, "PwshHooks")]
public class StartPwshHooksCmdlet : PSCmdlet
{
  [Parameter(Position = 0)]
  public string PipeName { get; set; } = "PwshHooks-" + Environment.UserName;

  // Runs in the background for the life of the module
  protected override void ProcessRecord() => _ = new Server().StartAsync(PipeName!);
}