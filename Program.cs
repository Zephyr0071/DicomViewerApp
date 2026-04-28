using System;
using System.Windows.Forms;


namespace DicomViewerApp
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();

            // Boot directly into the Universal Suite!
            Application.Run(new Form1());
        }
    }
}