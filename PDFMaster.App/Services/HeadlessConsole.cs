using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PDFMaster.Batch;
using PDFMaster.Infrastructure;

namespace PDFMaster.App.Services;

/// <summary>
/// Execução pela linha de comando, sem janela. O programa é um aplicativo de janela; para mostrar as
/// mensagens no terminal ele se liga ao console de quem o chamou (ou usa a saída redirecionada).
/// </summary>
public static class HeadlessConsole
{
    private const int AttachParentProcess = -1;

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        TextWriter console;
        if (Console.IsOutputRedirected)
        {
            // Saída redirecionada (scripts, agendador): sempre UTF-8, para acentos e setas chegarem intactos.
            console = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        }
        else
        {
            AttachConsole(AttachParentProcess);
            console = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // Sem console: as mensagens vão só para o --log.
            }

            console.WriteLine();
        }

        CliCommand command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (ArgumentException ex)
        {
            console.WriteLine("Erro nos parâmetros: " + ex.Message);
            console.Flush();
            return CommandLine.InvalidArguments;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            var services = new AutomationServices(new AppPaths().DataDirectory);
            return await CommandLine.RunAsync(command, services, console, cancel.Token);
        }
        finally
        {
            console.Flush();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
