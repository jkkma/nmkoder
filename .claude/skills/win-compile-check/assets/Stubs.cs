// Stubs for what the files under check touch from the rest of the app. Keep them honest:
// matching namespaces and signatures, no behaviour. If a build error lands in this file,
// the stub is wrong - fix it here, not in the app.
using Avalonia.Controls;

namespace Nmkoder.IO
{
    public static class Logger
    {
        public static void Log(string msg, bool hidden = false, bool replaceLastLine = false) { }
    }
}

namespace Nmkoder
{
    public static class Program
    {
        public static Window MainWin;
    }
}
