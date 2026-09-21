using Tpcli.Runtime;

try
{
    var app = RuntimeApplication.Build(args);
    await app.RunAsync();
}
catch
{
    Console.Error.WriteLine("RUNTIME_STARTUP_OR_HOST_FAILURE");
    Environment.ExitCode = 1;
}

public partial class Program;
