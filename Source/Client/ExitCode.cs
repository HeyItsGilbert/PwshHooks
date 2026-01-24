namespace PowerServe.Client;

public enum ExitCode
{
	Success = 0,
	GeneralError = 1,
	MissingArgument = 2,
	InvalidArgument = 3,
	FileNotFound = 4,
	AccessDenied = 5,
	ConnectionFailed = 6,
	Timeout = 7,
	InvalidOperation = 8,
	NotImplemented = 9,
	NotSupported = 10,
	UnknownError = 255
}

public static class ExitCodeMapper
{

	/// <summary>
	/// Converts an exception to its corresponding exit code for the program to have graceful exits.
	/// </summary>
	public static ExitCode GetExitCode(Exception ex)
	{
		return ex switch
		{
			ArgumentNullException or ArgumentException => ExitCode.InvalidArgument,
			FileNotFoundException => ExitCode.FileNotFound,
			UnauthorizedAccessException => ExitCode.AccessDenied,
			TimeoutException => ExitCode.Timeout,
			NotImplementedException => ExitCode.NotImplemented,
			NotSupportedException => ExitCode.NotSupported,
			_ => ExitCode.GeneralError
		};
	}

}
