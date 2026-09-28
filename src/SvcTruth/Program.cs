using System.Runtime.InteropServices;

namespace SvcTruth;

public static class Program
{
    [DllImport("libc")]
    private static extern uint getuid();

    /// <summary>The current user's uid, used to address the gui domain.</summary>
    public static uint CurrentUid() => getuid();

    public static async Task<int> Main(string[] args)
    {
        var exitCode = await SvcTruthApp.Run(
            args,
            CurrentUid(),
            new ProcessCommandRunner(),
            new RealFileSystem(),
            new RealClock(),
            Console.Out,
            Console.Error);
        return exitCode;
    }
}
