using System.Runtime.InteropServices;

namespace GrannyModelTool
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                AttachConsole(-1);
                return Cli.Run(args);
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
    }
}
