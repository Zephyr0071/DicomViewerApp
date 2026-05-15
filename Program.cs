using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;


namespace DicomViewerApp
{
    internal static class Program
    {
        // ✨ Import the low-level Windows API to fix .NET 8's blindspot
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        static void Main()
        {
            // ✨ THE MAGIC BULLET ✨
            // Force Windows to look inside your exact .exe folder when the VTK C++ files ask for their dependencies.
            SetDllDirectory(AppDomain.CurrentDomain.BaseDirectory);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Note: If you are on .NET 8, it might use ApplicationConfiguration.Initialize(); 
            // If your old Program.cs used that, you can replace the two lines above with it!

            Application.Run(new Form1());
        }
    }
}