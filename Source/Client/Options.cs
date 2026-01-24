using System.CommandLine;
using System.CommandLine.Parsing;

namespace PowerServe.Client;

/// <summary>
/// Holds the command line options for the PowerServe client in a
/// </summary>
public static class Options
{
	public static readonly Option<string> Script = new("--script", "-c")
	{
		Description = "A PowerShell script string to execute. Example: \"'Hello World'\""
	};

	public static readonly Option<FileInfo> File = new("--file", "-f")
	{
		Description = "Path to a PowerShell script file to execute",
	};

	public static readonly Option<DirectoryInfo> WorkingDirectory = new("--working-directory", "-w")
	{
		Description = "The working directory to use when spawning the PowerServe server",
		DefaultValueFactory = x => new DirectoryInfo(Environment.CurrentDirectory)
	};

	public static readonly Option<string> PipeName = new("--pipe-name", "-p")
	{
		Description = "The named pipe to use. The server will start here if not already running",
		DefaultValueFactory = x => $"PowerServe-{Environment.UserName}"
	};

	public static readonly Option<bool> Verbose = new("--verbose", "-v")
	{
		Description = "Log verbose messages about what PowerServeClient is doing to stderr. This may interfere with the JSON response so only use for troubleshooting"
	};

	public static readonly Option<int> Depth = new("--depth", "-d")
	{
		Description = "The maximum depth for JSON serialization of PowerShell objects. Set to a higher value if you need more in-depth information but this may slow processing, it is better to keep your objects shallow if possible.",
		DefaultValueFactory = x => 2
	};

	public static readonly Option<DirectoryInfo> ExeDir = new("--exeDir", "-e")
	{
		Description = "The directory to use to find pwsh.exe when spawning the PowerServe server if needed. Defaults to the directory of the current executable",
		DefaultValueFactory = x => new DirectoryInfo(Environment.CurrentDirectory)
	};

	// Setup validators for the options
	public static void Initialize()
	{
		File.AcceptExistingOnly();
		WorkingDirectory.AcceptExistingOnly();
		ExeDir.AcceptExistingOnly();
	}
}

