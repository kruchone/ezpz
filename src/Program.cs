using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ezpz
{
    static class Program
    {
        public static readonly string Version =
            ((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(Program).Assembly, typeof(AssemblyInformationalVersionAttribute))).InformationalVersion;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Unexpected error:\n\n" + e.Exception.Message, "ezpz", MessageBoxButtons.OK, MessageBoxIcon.Error);
            var f = new MainForm();
            if (args.Length == 2 && args[0] == "--page") f.StartPage = int.Parse(args[1]);
            Application.Run(f);
        }
    }
}

