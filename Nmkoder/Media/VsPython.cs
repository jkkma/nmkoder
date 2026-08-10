using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// The plumbing shared by the VapourSynth-scored metrics - <see cref="Ssimulacra2"/> and
    /// <see cref="Butteraugli"/> - which differ only in the plugin they call and the property they
    /// read. Everything about *running* a scoring script is identical between them, and two copies of
    /// it would have drifted the moment either was touched: which interpreter, how it is launched, and
    /// how a path becomes a Python string literal are one set of decisions, not two.
    /// </summary>
    internal static class VsPython
    {
        public static string VsynthDir()
        {
            return Path.Combine(Paths.GetBinPath(), "av1an", "vsynth");
        }

        /// <summary>
        /// The Python that will run a scoring script, and whether it is the bundled embeddable one.
        /// <para/>
        /// The bundle is Windows-only, so off Windows - and on a Windows machine using its own
        /// VapourSynth install - this falls back to a system interpreter found on PATH, python3 first
        /// since that is what a Linux VapourSynth builds against. That fallback is what makes the
        /// advertised "install it through your package manager" route real: without it, ResolveExecutable
        /// hands back the bare name "python" as its last resort and a File.Exists on that is always
        /// false, so the probe never ran and the run refused forever whatever the user installed.
        /// <para/>
        /// The bundled/system distinction is load-bearing for how the process is launched - see
        /// <see cref="Run"/> - because the bundle needs its own folder pinned onto PATH and as the
        /// working directory, where a system Python needs the inherited environment left alone so its
        /// own vapoursynth module and native libraries resolve.
        /// </summary>
        public static (string path, bool bundled) ResolvePython()
        {
            string bundled = Path.Combine(VsynthDir(), Shell.IsWindows ? "python.exe" : "python");

            if (File.Exists(bundled))
                return (bundled, true);

            foreach (string name in new[] { "python3", "python" })
            {
                string resolved = Shell.ResolveExecutable(name, PathDirs());

                if (File.Exists(resolved)) // A real file, not ResolveExecutable's bare-name fallback
                    return (resolved, false);
            }

            return ("", false);
        }

        /// <summary> The directories on PATH, for resolving a system Python. </summary>
        private static string[] PathDirs()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Shell.PathSeparator)
                .Where(x => x.IsNotEmpty()).ToArray();
        }

        /// <summary>
        /// Runs a Python script and returns its exit code and combined output.
        /// <para/>
        /// The bundled embeddable interpreter needs the vsynth folder pinned as both the working
        /// directory and the sole PATH entry, so it finds its own DLLs and the vapoursynth module - the
        /// same launch <see cref="VshipStager.ProbeDll"/> makes. A system Python is the opposite: its
        /// environment is left inherited untouched, or its own vapoursynth module and native libraries
        /// would not resolve. The process type decides whether Stop can kill it - Primary for a scoring
        /// run, Background for the short probes.
        /// </summary>
        public static async Task<(int exitCode, string output)?> Run(string python, bool bundled, string args, int timeoutMs, NmkoderProcess.ProcessType type)
        {
            System.Diagnostics.Process proc = OsUtils.NewProcess(true, type, python);

            if (bundled)
            {
                string vsynth = VsynthDir();
                OsUtils.SetPathVar(proc, new[] { vsynth });

                if (Directory.Exists(vsynth))
                    proc.StartInfo.WorkingDirectory = vsynth;
            }

            proc.StartInfo.Arguments = args;
            Logger.Log($"Running: {python} {args}", true);
            proc.Start();

            // Both pipes at once - reading one to the end first deadlocks as soon as the other fills
            // its buffer, the caveat the av1an help call, the QTGMC runner and the Vship probe all note.
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            Task both = Task.WhenAll(stdout, stderr);

            if (await Task.WhenAny(both, Task.Delay(timeoutMs)) != both)
            {
                OsUtils.KillProcessTree(proc.Id);
                return null;
            }

            await proc.WaitForExitAsync();
            return (proc.ExitCode, $"{stdout.Result}\n{stderr.Result}");
        }

        public static string PyString(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary> The open_video helper both scoring scripts start with: LSMASH, the same source
        /// plugin av1an and QTGMC use, cached into the caller's index directory. </summary>
        public const string OpenVideoSnippet = @"def open_video(path):
    attempts = [
        lambda: core.lsmas.LWLibavSource(source=path, cachedir=CACHE),
        lambda: core.lsmas.LWLibavSource(source=path),
    ]
    problems = []
    for attempt in attempts:
        try:
            return attempt()
        except Exception as e:
            problems.append(str(e))
    raise RuntimeError('no source plugin could open %r: %s' % (path, '; '.join(problems)))
";
    }
}
