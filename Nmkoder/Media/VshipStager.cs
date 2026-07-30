using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.OS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// Decides, per machine, whether the bundled Vship GPU plugin belongs in VapourSynth's
    /// autoload folder, and puts it there or takes it out accordingly.
    /// <para/>
    /// The release parks both vendor builds in vsynth/vship, outside the autoload folder,
    /// because presence is all av1an checks: the NVIDIA build imports nothing beyond KERNEL32,
    /// so it loads and registers on machines with no NVIDIA GPU at all, av1an would prefer it
    /// over the CPU plugins, and every probe would then fail mid-encode with no fallback.
    /// Whether a build can actually run here is decided by Vship's own GpuInfo kernel check -
    /// not by a vendor lookup, whose answer would be wrong for the real holes in the AMD
    /// build's architecture coverage - run through the bundled Python in a child process, so
    /// a crashing or hanging driver stack takes the probe down rather than the app.
    /// <para/>
    /// The copy this class stages carries its own name, nmkoder-vship_*.dll, so that a Vship
    /// the user installs - under upstream's file names or any other - is never mistaken for
    /// it. Anything Vship-shaped that is not nmkoder's own staged copy wins outright: the
    /// staged copy is withdrawn and the user's file is never touched.
    /// </summary>
    class VshipStager
    {
        /// <summary> The parked builds, under upstream's own file names, probe order first. </summary>
        private static readonly string[] ParkedDlls = { "libvship_NVIDIA.dll", "libvship_AMD.dll" };

        /// <summary> Parked while a probe runs, so the probe's core cannot autoload the staged copy -
        /// both builds register the same plugin identity, and a second load of it fails as a
        /// duplicate, which would read as this machine flunking a check its GPU passes. </summary>
        private const string BakSuffix = ".probing.bak";

        /// <summary>
        /// The parked build that passed this session's GPU check: a file name, "" when the checks
        /// answered and none passed, null when that is not yet known. Cached like av1an's help
        /// text - once per session - but only definite answers are kept, so a probe that timed
        /// out (a driver still paging in, say) is asked again on the next encode.
        /// </summary>
        private static string capableDll = null;

        /// <summary> This session's verdict so far: true = a build passed, false = checks answered
        /// and none passed, null = not yet known. For messages that say why a mode was stopped. </summary>
        public static bool? SessionVerdict { get { return capableDll == null ? (bool?)null : capableDll != ""; } }

        /// <summary> Whether the release's parked builds are present at all - an install without
        /// them (Linux, macOS, an older bundle) has nothing to stage and nothing to blame a GPU for. </summary>
        public static bool HasParkedBuilds()
        {
            try
            {
                string parked = Path.Combine(Paths.GetBinPath(), "av1an", "vsynth", "vship");
                return Directory.Exists(parked) && ParkedDlls.Any(d => File.Exists(Path.Combine(parked, d)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Brings the autoload folder in line with what this machine can run. Called before a
        /// metric-targeted encode, because av1an snapshots its plugin list once at startup, and
        /// because both SSIMULACRA2 and Butteraugli change scoring backend when Vship is present.
        /// An install without the parked folder changes nothing.
        /// </summary>
        public static async Task Reconcile()
        {
            try
            {
                string vsynth = Path.Combine(Paths.GetBinPath(), "av1an", "vsynth");
                string plugins = Path.Combine(vsynth, "vs-plugins");
                string parked = Path.Combine(vsynth, "vship");

                if (!Directory.Exists(plugins))
                    return;

                RestoreOrphanedBaks(plugins); // A crash mid-probe must not strand the staged copy

                if (!Directory.Exists(parked))
                    return;

                // A Vship the user installed wins outright, whatever they called it - the staged
                // copy is nmkoder's own name, so everything else Vship-shaped is theirs. Leaving
                // the staged copy beside it would not be neutral: both register the same plugin
                // identity, autoload order decides which one actually loads, and the frozen
                // bundled build sorting first would silently shadow the user's newer one.
                bool userOwn = Directory.EnumerateFiles(plugins)
                    .Select(f => Path.GetFileName(f))
                    .Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && f.ToLower().Contains("vship") && !IsManagedName(f));

                if (userOwn)
                {
                    if (Unstage(plugins))
                        Logger.Log("Withdrew the bundled Vship copy - the Vship you installed is in charge.");

                    return;
                }

                bool hadStaged = ManagedStagedFiles(plugins).Any();

                if (capableDll == null)
                {
                    capableDll = await FindCapableDllCleanly(plugins, parked, vsynth);

                    if (capableDll == null) // Unanswered - leave the folder as it is, ask again next time
                        return;

                    if (capableDll.IsEmpty() && hadStaged)
                        Logger.Log("Disabled the bundled Vship GPU plugin - no GPU here passes its check anymore.");
                }

                if (capableDll.IsEmpty())
                    Unstage(plugins);
                else
                    Stage(plugins, Path.Combine(parked, capableDll));
            }
            catch (Exception e)
            {
                Logger.Log($"Could not reconcile the Vship plugin: {e.Message}", true);
            }
        }

        /// <summary> The staged counterpart of a parked build: nmkoder's own name for it. </summary>
        private static string StagedName(string parkedName)
        {
            return "nmkoder-vship_" + parkedName.Split('_').Last();
        }

        private static bool IsManagedName(string fileName)
        {
            return ParkedDlls.Any(p => string.Equals(fileName, StagedName(p), StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> ManagedStagedFiles(string pluginsDir)
        {
            return ParkedDlls.Select(p => Path.Combine(pluginsDir, StagedName(p))).Where(File.Exists).ToList();
        }

        /// <summary>
        /// Runs the GPU checks against a plugin folder that is guaranteed not to answer for them:
        /// the staged copy is renamed aside first (autoload only takes ".dll", so a ".bak" is
        /// invisible), because a core that has already autoloaded it fails the probe's own load
        /// of the parked build as a duplicate - and a healthy machine would then read as one
        /// whose GPU flunked. Renamed back when the checks go unanswered, so an unknown verdict
        /// leaves the world exactly as it was; removed once a definite verdict will restage or
        /// unstage anyway. A copy that cannot be moved (a leftover vspipe still holds it) makes
        /// the probe unanswerable rather than wrong.
        /// </summary>
        private static async Task<string> FindCapableDllCleanly(string pluginsDir, string parkedDir, string vsynthDir)
        {
            List<string> baks = new List<string>();

            foreach (string staged in ManagedStagedFiles(pluginsDir))
            {
                try
                {
                    File.Move(staged, staged + BakSuffix, true);
                    baks.Add(staged);
                }
                catch (Exception e)
                {
                    Logger.Log($"Cannot check the GPU now - {Path.GetFileName(staged)} is in use: {e.Message}", true);
                    RestoreBaks(baks);
                    return null;
                }
            }

            string verdict = await FindCapableDll(parkedDir, vsynthDir);

            foreach (string staged in baks)
            {
                // An unanswered check puts everything back. A pass puts its own copy back too -
                // it is the same bytes Stage would otherwise copy again, so the re-check a new
                // session runs does not cost a fresh 25 MB write. Only a copy the verdict
                // disowned is dropped.
                bool keep = verdict == null || (verdict.IsNotEmpty()
                    && string.Equals(Path.GetFileName(staged), StagedName(verdict), StringComparison.OrdinalIgnoreCase));

                if (keep)
                    RestoreBak(staged);
                else
                    TryDelete(staged + BakSuffix);
            }

            return verdict;
        }

        private static void RestoreBaks(List<string> stagedPaths)
        {
            foreach (string staged in stagedPaths)
                RestoreBak(staged);
        }

        private static void RestoreBak(string stagedPath)
        {
            try
            {
                File.Move(stagedPath + BakSuffix, stagedPath, true);
            }
            catch (Exception e)
            {
                Logger.Log($"Could not restore {Path.GetFileName(stagedPath)}: {e.Message}", true);
            }
        }

        /// <summary> Cleans up after a session that died mid-probe: a stranded ".bak" is put back
        /// where it was, or dropped when a live copy already took its place. </summary>
        private static void RestoreOrphanedBaks(string pluginsDir)
        {
            foreach (string parked in ParkedDlls)
            {
                string staged = Path.Combine(pluginsDir, StagedName(parked));
                string bak = staged + BakSuffix;

                if (!File.Exists(bak))
                    continue;

                if (File.Exists(staged))
                    TryDelete(bak);
                else
                    try { File.Move(bak, staged); } catch { TryDelete(bak); }
            }
        }

        /// <summary>
        /// The first parked build whose GPU check passes, NVIDIA first (it loads everywhere, so
        /// its verdict is always the GPU's own). "" when at least one build was asked and every
        /// one that answered said no; null when nothing could be asked or an answer is missing,
        /// which concludes nothing.
        /// </summary>
        private static async Task<string> FindCapableDll(string parkedDir, string vsynthDir)
        {
            bool anyUnknown = false;
            bool anyAnswered = false;

            foreach (string name in ParkedDlls)
            {
                string dll = Path.Combine(parkedDir, name);

                if (!File.Exists(dll))
                    continue;

                bool? passed = await ProbeDll(dll, vsynthDir);

                if (passed == true)
                    return name;

                if (passed == null)
                    anyUnknown = true;
                else
                    anyAnswered = true;
            }

            return anyUnknown || !anyAnswered ? null : "";
        }

        // The probe never decides in Python - it only reports. Loading can fail (the AMD build
        // hard-imports the HIP runtime that only AMD's driver installs), GpuInfo can fail (no
        // device), and a present device can still flunk the kernel check (an architecture the
        // fat binary does not carry) - all three are the same "not this machine" answer. The
        // kernel check line is matched leniently because the DLL prints it with a space before
        // the colon ("PassKernelCheck : 1"), and that formatting is nobody's contract.
        private const string ProbeScript = @"import sys, re
try:
    import vapoursynth
    core = vapoursynth.core
    core.std.LoadPlugin(path=sys.argv[1])
except Exception as e:
    print('VSHIP_FAIL load:', e)
    sys.exit(0)
try:
    info = str(core.vship.GpuInfo(gpu_id=0))
    print('VSHIP_OK' if re.search(r'PassKernelCheck\s*:\s*1\b', info) else 'VSHIP_FAIL kernelcheck:', info)
except Exception as e:
    print('VSHIP_FAIL gpu:', e)
";

        /// <summary>
        /// Asks a parked build whether this machine's first GPU can run it - gpu_id 0 exactly,
        /// because that is the device av1an's scoring will use. True/false are Vship's own
        /// answer; null means the question went unanswered (no bundled Python, a timeout, a
        /// crashed probe) and should be asked again rather than acted on.
        /// </summary>
        private static async Task<bool?> ProbeDll(string dllPath, string vsynthDir)
        {
            try
            {
                string python = Path.Combine(vsynthDir, "python.exe");

                if (!File.Exists(python))
                    return null;

                string script = Path.Combine(Paths.GetSessionDataPath(), "vship_probe.py");
                Directory.CreateDirectory(Path.GetDirectoryName(script));
                File.WriteAllText(script, ProbeScript);

                Process probe = OsUtils.NewProcess(true, NmkoderProcess.ProcessType.Background, python);
                OsUtils.SetPathVar(probe, new[] { vsynthDir });
                probe.StartInfo.WorkingDirectory = vsynthDir;
                probe.StartInfo.Arguments = $"{script.Wrap()} {dllPath.Wrap()}";
                probe.Start();

                // Both pipes at once - reading one to the end first deadlocks once the other
                // fills its buffer, the same caveat the av1an help call documents.
                Task<string> stdout = probe.StandardOutput.ReadToEndAsync();
                Task<string> stderr = probe.StandardError.ReadToEndAsync();
                Task both = Task.WhenAll(stdout, stderr);

                // Generous, because the first CUDA/HIP init after boot can sit behind driver
                // paging; a hung driver stack is converted into "unknown" rather than a hang.
                if (await Task.WhenAny(both, Task.Delay(15000)) != both)
                {
                    Logger.Log($"Vship GPU check did not answer in time ({Path.GetFileName(dllPath)}).", true);
                    OsUtils.KillProcessTree(probe.Id);
                    return null;
                }

                string output = $"{stdout.Result}\n{stderr.Result}";
                string trimmed = output.Trim();
                Logger.Log($"Vship GPU check ({Path.GetFileName(dllPath)}): {(trimmed.Length > 300 ? trimmed.Substring(0, 300) : trimmed)}", true);

                if (output.Contains("VSHIP_OK"))
                    return true;

                if (output.Contains("VSHIP_FAIL"))
                    return false;

                return null; // The probe died without reporting - concludes nothing
            }
            catch (Exception e)
            {
                Logger.Log($"Vship GPU check failed to run: {e.Message}", true);
                return null;
            }
        }

        /// <summary>
        /// Copies the capable build into the autoload folder under nmkoder's own name and removes
        /// the other vendor's staged copy first - both register the same plugin identity, so
        /// exactly one may be loadable, and staging is abandoned when the old one cannot be
        /// removed rather than risking the pair. The copy lands under a temp name and is moved
        /// into place, so a copy that dies half-way never leaves a truncated ".dll" that would
        /// satisfy the guard without loading. Skipped when an identical-sized copy is already in
        /// place, which is every run after the first.
        /// </summary>
        private static void Stage(string pluginsDir, string sourceDll)
        {
            string target = Path.Combine(pluginsDir, StagedName(Path.GetFileName(sourceDll)));
            string other = ParkedDlls.Select(p => Path.Combine(pluginsDir, StagedName(p))).First(p => p != target);

            if (File.Exists(other) && !TryDelete(other))
                return; // Held open by a leftover process - retried on the next encode

            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(sourceDll).Length)
                return;

            string tmp = target + ".tmp";

            try
            {
                File.Copy(sourceDll, tmp, true);
                File.Move(tmp, target, true);
                Logger.Log($"Enabled the bundled Vship GPU plugin for metric scoring ({Path.GetFileName(sourceDll)}).");
            }
            catch (Exception e)
            {
                // A vspipe from an earlier run may still hold the old file; the next encode retries.
                Logger.Log($"Could not stage the Vship plugin: {e.Message}", true);
                TryDelete(tmp);
            }
        }

        /// <summary>
        /// Removes the staged copies. Load-bearing rather than tidy: a staged NVIDIA build still
        /// loads and registers after that GPU is gone, av1an would keep preferring it, and every
        /// probe would fail mid-encode - so a machine that flunks the check has to actually lose
        /// the plugin, not just stop gaining it. Returns whether anything was removed; whoever
        /// calls it knows why, so the saying-so is theirs.
        /// </summary>
        private static bool Unstage(string pluginsDir)
        {
            bool removed = false;

            foreach (string staged in ManagedStagedFiles(pluginsDir))
                removed |= TryDelete(staged);

            return removed;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not remove {Path.GetFileName(path)}: {e.Message}", true);
                return false;
            }
        }
    }
}
