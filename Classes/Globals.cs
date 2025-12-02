using reactivo.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace reactivo.Classes
{
    public static class Globals
    {
        public static WebScket webSocket = new WebScket();
        public static NamedPipe namedPipe = new NamedPipe();

        public static void Announce(string message)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msg.exe",
                Arguments = $"* \"{message}\"",
                CreateNoWindow = true
            });
        }
    }
}
