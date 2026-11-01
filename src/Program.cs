using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace MultiClaude;

internal static class Program
{
    public const string AppId = "MultiClaude.App";
    public const string HomePage = "https://github.com/tcollins590/multiclaude-windows";

    /// <summary>
    /// MultiClaude.exe               start the tray app (or show its panel if it already runs)
    /// MultiClaude.exe --open ID     open or focus a profile; used by Start menu shortcuts and pinned buttons
    /// MultiClaude.exe --startup     start quietly at sign-in
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-app-icon") { Icons.WriteAppIcon(args[1]); return 0; }

        string? open = null;
        bool startup = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--open" && i + 1 < args.Length) open = args[++i];
            else if (args[i] == "--startup") startup = true;
        }

        using var mutex = new Mutex(true, @"Local\MultiClaude.Tray." + UserKey, out bool first);
        if (!first)
        {
            PipeServer.Send(open != null ? "open " + open : startup ? "" : "show");
            return 0;
        }

        Native.SetCurrentProcessExplicitAppUserModelID(AppId);
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(open));
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>Keeps the mutex and pipe per Windows user.</summary>
    public static string UserKey { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..12];
}

/// <summary>Lets a second launch (a shortcut, a pinned button) hand its request to the running tray app.</summary>
internal static class PipeServer
{
    static string Name => "MultiClaude." + Program.UserKey;

    public static void Start(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = reader.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line)) onMessage(line.Trim());
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "MultiClaude pipe" };
        thread.Start();
    }

    public static void Send(string message)
    {
        if (message.Length == 0) return;
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(message);
        }
        catch { }
    }
}
