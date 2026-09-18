using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HsReconnectorApp
{
    internal static class Program
    {
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [STAThread]
        private static void Main()
        {
            // The app runs elevated and is often started straight from the Downloads folder. By
            // default Windows looks for a DLL next to the exe before System32, so a DLL dropped
            // there (uxtheme.dll, dwmapi.dll, ...) would run with administrator rights. From here
            // on, every DLL that is not already loaded comes from System32 only. Failure is not
            // fatal: the call exists on every supported Windows version.
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
