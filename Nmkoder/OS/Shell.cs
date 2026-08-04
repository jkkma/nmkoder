using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Nmkoder.OS
{
    /// <summary>
    /// Abstracts away the platform's command interpreter. The original code hardcoded
    /// "cmd.exe /C ..." everywhere; on Linux/macOS we need "/bin/sh -c ..." instead.
    /// </summary>
    public static class Shell
    {
        public static bool IsWindows { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); } }

        /// <summary> Executable used to run a command line. </summary>
        public static string Interpreter { get { return IsWindows ? "cmd.exe" : "/bin/sh"; } }

        /// <summary> Flag that tells the interpreter that the next argument is the command to run. </summary>
        public static string RunFlag(bool stayOpen = false)
        {
            if (IsWindows)
                return stayOpen ? "/K" : "/C";

            return "-c";
        }

        /// <summary> Directory separator used inside shell commands. </summary>
        public static char PathSeparator { get { return IsWindows ? ';' : ':'; } }

        /// <summary> Builds a "change directory" prefix for a chained shell command. </summary>
        public static string ChangeDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return "";

            return IsWindows ? $"cd /D \"{dir}\" &" : $"cd \"{dir}\" &&";
        }

        /// <summary> Path of the platform's null device, for -f null targets and output redirection. </summary>
        public static string NullDevice { get { return IsWindows ? "NUL" : "/dev/null"; } }

        /// <summary>
        /// Neutralises the substitutions the interpreter would otherwise perform on a command line
        /// that has to be embedded in a script. Quoting alone does not stop them: cmd still expands
        /// %VAR% inside double quotes, and sh still expands $var and backticks - so a file named
        /// "My $Movie.mkv" silently loses part of its path, and one containing `...` would run it.
        /// Only apply this to text that is data (paths, arguments), never to a line that needs its
        /// own expansions, such as "SET PATH=...;%PATH%".
        /// <para/>
        /// The Windows branch is correct **only for a batch file**, which is the one thing that calls
        /// it - AvProcess's av1an launch script. "%%" means one literal "%" to a .bat line and to
        /// nothing else: on a "cmd /C" command line it is not collapsed, so a path escaped this way
        /// arrives at the tool with its percents doubled. Microsoft says so where it documents
        /// command-line expansion - "Escaping a % character as %%, the way you can do inside batch
        /// files, isn't supported" - and draws the same line for "for", which takes %f at the prompt
        /// and %%f in a batch file. **Do not reach for this from <see cref="WrapArg"/>.**
        /// <para/>
        /// The script needs it for the other half of that asymmetry: in a batch file an undefined
        /// %NAME% is deleted outright, where a command line passes it through untouched.
        /// </summary>
        public static string EscapeExpansions(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (IsWindows)
                return text.Replace("%", "%%");

            return text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("`", "\\`");
        }

        /// <summary>
        /// A path - or any other value, a filtergraph among them - as one argument, which the
        /// interpreter hands on exactly as it was given.
        /// <para/>
        /// Quoting alone does not do that on Linux or macOS: sh expands $var and backticks inside
        /// double quotes, so a file named "My $HOME clip.mkv" reached ffmpeg as a path that does not
        /// exist, and one with backticks in its name ran whatever was between them. Single quotes
        /// suppress every expansion there is. The two characters a single-quoted run cannot carry are
        /// handled by leaving it and coming back - a quote through a double-quoted one, and a backslash
        /// through a double-quoted "\\", whose doubling is exactly what <see cref="BuildArguments"/>'s
        /// own escaping and sh's collapsing hand between them.
        /// <para/>
        /// That last part is why <see cref="EscapeExpansions"/> is not the answer here, though it looks
        /// like it: it works for the av1an launch script, which is written to a file, and cannot work
        /// through BuildArguments, which doubles every backslash - so the single "\$" that would make
        /// sh leave the dollar alone is not a string this layer can produce at all.
        /// <para/>
        /// Measured rather than reasoned out. This encoding round-trips $, backticks, both quote
        /// characters, single and double backslashes, %, &amp;, !, ;, newlines, spaces and parentheses
        /// through the whole chain of .NET argument parsing and sh.
        /// <para/>
        /// Windows keeps the plain double quotes it has always used, and cmd *does* expand %VAR%
        /// inside them - but there is no escape to reach for and the exposure is narrow, so it is
        /// left alone deliberately rather than overlooked. Narrow, because a command line passes an
        /// **undefined** %NAME% through unchanged, unlike a batch file, which deletes it: so
        /// "Show%20-%2001.mkv" and "50% off.mkv" both survive, and only a real variable name between
        /// two percents - %TEMP%, or a dynamic one like %CD% or %DATE% - is substituted, which then
        /// fails loudly naming a path nobody typed. No escape, because "%%" is a batch-file spelling
        /// that a command line does not collapse (see <see cref="EscapeExpansions"/>), and "^" is
        /// literal data inside double quotes - % expansion runs before the caret phase in any case.
        /// <para/>
        /// Doubling the percents here would corrupt every %-bearing path *and* break Quick Convert's
        /// image sequences, whose output path is "%8d.&lt;ext&gt;" and comes through this method:
        /// measured, ffmpeg answers "%%8d.png" with "Cannot write more than one file with the same
        /// name" and writes a single file literally called %%8d.png. The way out, if this ever
        /// matters, is to stop using cmd for the launches that need no shell - AvProcess does exactly
        /// that for av1an - not to escape anything here.
        /// </summary>
        public static string WrapArg(string arg)
        {
            arg = arg ?? "";

            if (IsWindows)
                return $"\"{arg}\"";

            return string.Join("\"\\\\\"", arg.Split('\\').Select(part => $"'{part.Replace("'", "'\"'\"'")}'"));
        }

        /// <summary>
        /// Pipes stderr into a line filter that keeps lines containing any of the given literals
        /// (findstr on Windows, grep -F elsewhere).
        /// </summary>
        public static string GrepStderr(params string[] literals)
        {
            if (IsWindows)
                return $"2>&1 1>{NullDevice} | findstr /L /C:\"{string.Join("\" /C:\"", literals)}\"";

            string patterns = string.Join(" ", literals.Select(l => $"-e \"{l}\""));
            return $"2>&1 1>{NullDevice} | grep -F {patterns}";
        }

        /// <summary>
        /// Wraps a command so it is executed by the platform interpreter.
        /// On POSIX the whole command must be a single argument, so it gets quoted.
        /// </summary>
        public static string BuildArguments(string command, bool stayOpen = false)
        {
            if (IsWindows)
                return $"{RunFlag(stayOpen)} {command}";

            return $"{RunFlag(stayOpen)} \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        /// <summary> Opens a file, folder or URL with the OS default handler. </summary>
        public static void OpenWithDefaultHandler(string target)
        {
            try
            {
                if (IsWindows)
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", new[] { target });
                }
                else
                {
                    Process.Start("xdg-open", new[] { target });
                }
            }
            catch (Exception e)
            {
                IO.Logger.Log($"Failed to open '{target}': {e.Message}", true);
            }
        }

        /// <summary> Finds an executable in the given directories, falling back to PATH. </summary>
        public static string ResolveExecutable(string name, IEnumerable<string> searchDirs)
        {
            string[] exts = IsWindows ? new[] { ".exe", ".bat", ".cmd", "" } : new[] { "", ".sh" };

            foreach (string dir in searchDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                foreach (string ext in exts)
                {
                    string candidate = Path.Combine(dir, name + ext);

                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return name; // Let the OS resolve it through PATH
        }
    }
}
