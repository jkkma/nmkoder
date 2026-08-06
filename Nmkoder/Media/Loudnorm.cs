using Nmkoder.Data;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Nmkoder.Media
{
    /// <summary>
    /// The measuring half of two-pass loudness normalization: one ffmpeg run per audio track that
    /// decodes it, prints what <c>loudnorm</c> made of it, and hands the numbers to
    /// <see cref="LoudnessConfig.GetFilter"/> for the encode to carry back in.
    /// <para/>
    /// It costs a full decode of the audio, which is cheap next to any video encode and is not cheap
    /// next to nothing - so it runs once per track per encode, and only when a target is selected.
    /// </summary>
    class Loudnorm
    {
        /// <summary>
        /// Measures one audio track as it will be written - the same channel layout, and the same
        /// section of it.
        /// <para/>
        /// <paramref name="trimArgs"/> is the trim's own seek and duration. It matters more than it
        /// looks: measured on a source whose first half is 26 dB quieter than its second, the whole file
        /// reads -19.2 LUFS and its quiet half alone reads -45.2, so a trim measured against the wrong
        /// span misses the target by as much as the trim removed.
        /// </summary>
        public static async Task<LoudnessMeasurement> MeasureAsync(string path, int audioStreamIndex, int channels, string trimArgs, LoudnessConfig config)
        {
            try
            {
                // -vn and the single audio map, so nothing decodes a video track to measure a sound one.
                string args = $"{trimArgs} -i {path.Wrap()} -map 0:a:{audioStreamIndex} -vn -sn " +
                    $"-af {config.GetMeasureFilter(channels).Wrap()} -f null -";

                var settings = new AvProcess.FfmpegSettings()
                {
                    Args = args,
                    // loudnorm prints its JSON at "info", so a quieter level measures nothing at all.
                    LogLevel = "info",
                    ProcessType = OS.NmkoderProcess.ProcessType.Secondary,
                    CanCancelTask = false,
                };

                string output = await AvProcess.RunFfmpeg(settings);
                LoudnessMeasurement measurement = Parse(output);

                if (measurement == null)
                    Logger.Log($"Could not measure the loudness of audio track {audioStreamIndex + 1} - FFmpeg printed no measurement.", true);

                return measurement;
            }
            catch (Exception e)
            {
                Logger.Log($"Measuring loudness failed: {e.Message}", true, level: Logger.Level.Debug);
                return null;
            }
        }

        /// <summary>
        /// Pulls the measurement out of ffmpeg's output.
        /// <para/>
        /// Located by finding the *last* brace pair rather than by parsing the whole stream: the JSON is
        /// the final thing loudnorm prints, and everything around it is ordinary ffmpeg chatter that has
        /// no braces of its own. Deserialized by hand rather than through a JSON library because every
        /// value is a bare number in a flat object, and because the numbers have to be read with the
        /// invariant culture whatever the machine's own is - loudnorm prints "-19.21" and a German
        /// locale's parser reads that as -1921.
        /// </summary>
        private static LoudnessMeasurement Parse(string output)
        {
            if (output.IsEmpty())
                return null;

            int start = output.LastIndexOf('{');
            int end = output.LastIndexOf('}');

            if (start < 0 || end < start)
                return null;

            Dictionary<string, double> values = new Dictionary<string, double>();

            foreach (string line in output.Substring(start, end - start + 1).SplitIntoLines())
            {
                string[] halves = line.Replace("\"", "").Replace(",", "").Split(':');

                if (halves.Length != 2)
                    continue;

                if (double.TryParse(halves[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    values[halves[0].Trim()] = value;
            }

            // All five or none: a partial read would put a plausible-looking number into the encode and
            // move the track to the wrong loudness, where a missing one falls back to not normalizing it
            // and says so.
            foreach (string key in new[] { "input_i", "input_tp", "input_lra", "input_thresh", "target_offset" })
            {
                if (!values.ContainsKey(key))
                    return null;
            }

            return new LoudnessMeasurement
            {
                InputI = values["input_i"],
                InputTp = values["input_tp"],
                InputLra = values["input_lra"],
                InputThresh = values["input_thresh"],
                TargetOffset = values["target_offset"],
            };
        }
    }
}
