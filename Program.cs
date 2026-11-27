using System.Diagnostics;
using System.Globalization;
using FileViewer.Forms;

namespace FileViewer;

internal static class Program
{
    internal const string DiagnosticArgument = "--diagnostic";
    internal const string NetworkArgument = "--network";

    internal static void RestartAsAdministrator(string? startupArgument = null)
    {
        var currentWindow = Application.OpenForms.OfType<MainForm>().FirstOrDefault()
            ?? throw new InvalidOperationException("The main window is unavailable.");
        string executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        if (startupArgument is not null)
            start.ArgumentList.Add(startupArgument);

        using var elevated = Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start a new Unit Manager process.");
        currentWindow.Close();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        ApplicationConfiguration.Initialize();
        using var splash = new SplashScreen();
        using var startup = new ApplicationContext(splash);
        MainForm? form = null;
        splash.Completed += (_, _) =>
        {
            form = new MainForm();
            if (args.Any(arg => arg.Equals(DiagnosticArgument, StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--diagnostico", StringComparison.OrdinalIgnoreCase)))
                form.ShowDiagnosticTab();
            else if (args.Any(arg => arg.Equals(NetworkArgument, StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--red", StringComparison.OrdinalIgnoreCase)))
                form.ShowNetworkTab();
            startup.MainForm = form;
            form.Show();
            splash.Close();
        };

        try
        {
            Application.Run(startup);
        }
        finally
        {
            form?.Dispose();
        }
    }
}
