using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Nmkoder.UI
{
    /// <summary>
    /// The output file name a batch gives each file it encodes.
    /// <para/>
    /// Batch mode overwrites the output box once per file - it has to, since twelve files cannot
    /// share one output path - which used to mean the box was simply ignored and every name came out
    /// of the source with nothing the user could do about it. The template is what the box would
    /// have been: it is resolved per file, so "{name}-av1" or "{name}_{codec}_{crf}" reaches all
    /// twelve. Only the file name is templated; the folder is still the default output directory, or
    /// the source's own folder when none is set.
    /// </summary>
    internal class BatchNaming
    {
        public const string DefaultTemplate = "{name}";

        /// <summary> The placeholders, in the order the hint lists them. </summary>
        public static readonly (string Token, string Description)[] Tokens =
        {
            ("{name}", "the source file name, without its extension"),
            ("{ext}", "the source file's extension, without the dot"),
            ("{index}", "its position in the queue, zero-padded to the queue's length"),
            ("{codec}", "the selected video encoder (av1, h265, vp9…)"),
            ("{crf}", "the quality value; {quality} means the same"),
            ("{preset}", "the selected encoder preset"),
            ("{width}", "the source's video width in pixels"),
            ("{height}", "the source's video height in pixels"),
            ("{date}", "today's date as yyyy-MM-dd"),
        };

        /// <summary> What the batch loop knows and the naming does not: which task is running, and
        /// where in the queue this file sits. Null whenever no batch is running. </summary>
        private class Context
        {
            public RunTask.TaskType Task;
            public int Index;
            public int Count;
        }

        private static Context _context = null;

        public static void SetContext(RunTask.TaskType task, int index, int count)
        {
            _context = new Context { Task = task, Index = index, Count = count };
        }

        public static void ClearContext()
        {
            _context = null;
        }

        /// <summary> The template as it stands in the File List tab, or the default when it is blank. </summary>
        public static string CurrentTemplate
        {
            get
            {
                string text = (Program.MainWin?.BatchNameTemplateBox.Text ?? "").Trim();
                return text.IsEmpty() ? DefaultTemplate : text;
            }
        }

        /// <summary>
        /// A worked example of what the template does, for the hint beside the box: what the first
        /// file in the queue would come out called. A list of placeholders describes the feature; one
        /// resolved name answers the question actually being asked.
        /// </summary>
        public static string DescribeExample(MediaFile file, RunTask.TaskType task)
        {
            if (file == null)
                return "Load a file to see an example.";

            // A batch that is running owns the context, and its files are worth more than a hint.
            if (RunTask.runningBatch)
                return "";

            Context previous = _context;

            try
            {
                _context = new Context { Task = task, Index = 1, Count = Math.Max(1, FileList.Items.Count) };
                string name = ResolveName(file.SourcePath);
                string ext = GetContainerExtension(task);
                return $"e.g. {Path.GetFileName(file.SourcePath)} → {name}{ext}";
            }
            catch (Exception e)
            {
                Logger.Log($"Could not preview the output name template: {e.Message}", true);
                return "";
            }
            finally
            {
                _context = previous;
            }
        }

        /// <summary> The extension the task's container box would add, dot included, or "" when the
        /// task writes something the box does not describe. </summary>
        private static string GetContainerExtension(RunTask.TaskType task)
        {
            var f = Program.MainWin;
            string container = task == RunTask.TaskType.Av1an ? f.Av1anContainerBox.GetText().Trim()
                : task == RunTask.TaskType.Convert ? f.FfmpegContainerBox.GetText().Trim() : "";

            return container.IsEmpty() ? "" : $".{container.Lower()}";
        }

        /// <summary>
        /// The base name (no extension, no folder) an output file should be given. Outside a batch
        /// this is the source's own name, which is what it has always been.
        /// </summary>
        public static string ResolveName(string sourcePath)
        {
            string sourceName = Path.GetFileNameWithoutExtension(sourcePath) ?? "";

            if (_context == null)
                return sourceName;

            try
            {
                string resolved = Sanitize(Apply(CurrentTemplate, sourcePath, sourceName));

                if (resolved.IsEmpty())
                {
                    Logger.Log($"The output name template produced an empty name for '{Path.GetFileName(sourcePath)}', so its own name is being used instead.");
                    return sourceName;
                }

                return resolved;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not apply the output name template ({e.Message}) - using '{sourceName}' instead.");
                return sourceName;
            }
        }

        private static string Apply(string template, string sourcePath, string sourceName)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "{name}", sourceName },
                { "{ext}", (Path.GetExtension(sourcePath) ?? "").TrimStart('.') },
                { "{index}", _context.Index.ToString(new string('0', Math.Max(1, _context.Count.ToString().Length))) },
                { "{codec}", GetCodecToken() },
                { "{crf}", GetQualityToken() },
                { "{quality}", GetQualityToken() },
                { "{preset}", GetPresetToken() },
                { "{width}", GetResolutionToken(sourcePath, width: true) },
                { "{height}", GetResolutionToken(sourcePath, width: false) },
                { "{date}", DateTime.Now.ToString("yyyy-MM-dd") },
            };

            string result = template;

            foreach (var pair in values)
                result = ReplaceCaseInsensitive(result, pair.Key, pair.Value);

            return result;
        }

        private static string ReplaceCaseInsensitive(string text, string token, string value)
        {
            for (int at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(token, at + value.Length, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, at) + value + text.Substring(at + token.Length);

                if (at + value.Length >= text.Length)
                    break;
            }

            return text;
        }

        /// <summary> "av1", "h265", "vp9" - the encoder's friendly name cut back to the part that
        /// names the format, since that is the part worth carrying in a file name. </summary>
        private static string GetCodecToken()
        {
            try
            {
                if (_context.Task == RunTask.TaskType.Av1an)
                    return ShortenCodecName(CodecUtils.GetCodec(Av1anUi.GetCurrentCodecV()).FriendlyName);

                if (_context.Task != RunTask.TaskType.Convert)
                    return "";

                // The two that are not codecs are named for what they do instead: their friendly
                // names are sentences, and "clip_copyvideowithoutreencoding.mkv" is nobody's idea
                // of a file name.
                CodecUtils.VideoCodec codec = QuickConvertUi.GetCurrentCodecV();

                if (codec == CodecUtils.VideoCodec.CopyVideo)
                    return "copy";

                if (codec == CodecUtils.VideoCodec.StripVideo)
                    return "novideo";

                return ShortenCodecName(CodecUtils.GetCodec(codec).FriendlyName);
            }
            catch
            {
                return "";
            }
        }

        /// <summary> "H.265 / HEVC (x265)" -> "h265", "AV1 (SVT-AV1)" -> "av1", "GIF [Animated GIF]" -> "gif" </summary>
        private static string ShortenCodecName(string friendlyName)
        {
            string head = (friendlyName ?? "").Split(new[] { " /", " (", " [" }, StringSplitOptions.None).First().Trim();
            return new string(head.Where(char.IsLetterOrDigit).ToArray()).Lower();
        }

        private static string GetQualityToken()
        {
            var f = Program.MainWin;

            if (_context.Task == RunTask.TaskType.Av1an)
            {
                decimal q = f.Av1anQualityUpDown.Value ?? 0;
                // Butteraugli and XPSNR targets are fractional, and "3.5" in a file name is fine
                // where "3" would name a different encode entirely.
                return (q == Math.Truncate(q) ? ((int)q).ToString() : q.ToString("0.0##", CultureInfo.InvariantCulture));
            }

            if (_context.Task == RunTask.TaskType.Convert)
                return f.EncVidQualityBox.Value.AsInt().ToString();

            return "";
        }

        private static string GetPresetToken()
        {
            var f = Program.MainWin;

            if (_context.Task == RunTask.TaskType.Av1an)
                return f.Av1anPresetBox.GetText().Trim().Lower();

            if (_context.Task == RunTask.TaskType.Convert)
                return f.EncVidPresetBox.GetText().Trim().Lower();

            return "";
        }

        /// <summary> The source's video size. Read off the loaded file rather than probed: the batch
        /// loads each file before it names its output, so this costs nothing. </summary>
        private static string GetResolutionToken(string sourcePath, bool width)
        {
            MediaFile file = TrackList.current?.File;

            if (file == null || file.SourcePath != sourcePath || file.VideoStreams.Count < 1)
                return "";

            Data.Size res = file.VideoStreams[0].Resolution;
            return (width ? res.Width : res.Height).ToString();
        }

        /// <summary>
        /// Makes the resolved name safe to write. Path separators go too, not just the platform's
        /// invalid characters: a template is a file name, and one that quietly created folders would
        /// scatter a queue's output across the disk.
        /// </summary>
        private static string Sanitize(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var sb = new StringBuilder();

            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);

            // A trailing dot or space is legal to build and impossible to open on Windows
            return sb.ToString().Trim().TrimEnd('.', ' ');
        }
    }
}
