using Newtonsoft.Json;
using Nmkoder.Data;
using Nmkoder.Data.Codecs;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Nmkoder.UI.Tasks
{
    /// <summary>
    /// The per-encoder argument grid behind both encode tabs' Advanced tab: the documented parameters
    /// an encoder has, read out of a JSON list shipped beside the binaries, and the values the user
    /// typed into them, kept per encoder in the config.
    /// <para/>
    /// Both tabs share the machinery and share nothing else. The lists live in different folders
    /// because they are different vocabularies rather than different files: the AV1AN tab drives
    /// standalone encoder binaries and its lists name their CLI parameters, where Quick Convert drives
    /// ffmpeg and its lists name what the *wrapper* accepts - which for VP9 and NVENC is not the same
    /// set of names at all. The values are kept apart for the same reason, under a key each.
    /// </summary>
    class EncoderArgs
    {
        /// <summary> Where the AV1AN tab's lists are filed, under <see cref="Paths.GetBinPath"/>. </summary>
        public const string Av1anFolder = "av1an";

        /// <summary> Where Quick Convert's lists are filed, under <see cref="Paths.GetBinPath"/>. </summary>
        public const string FfmpegFolder = "ffmpeg";

        /// <summary>
        /// The parameters documented for an encoder, as [argument, value, description, category,
        /// details, examples] rows. Values come through blank: the list is there to be read and
        /// filled in, and only rows with a value reach the command line. An encoder with no file
        /// simply has nothing to show. The category names the tab the row appears under; a row
        /// without one - the format before categories existed - is grouped as "Other" rather than
        /// dropped. Details and examples feed the right-click window and may be absent.
        /// </summary>
        public static List<EncoderArgRow> ReadRows(IEncoder enc, string folder)
        {
            List<EncoderArgRow> rows = new List<EncoderArgRow>();
            string jsonPath = Path.Combine(Paths.GetBinPath(), folder, "encoderArgs", enc.Name + ".json");

            if (!File.Exists(jsonPath))
                return rows;

            List<string[]> args;

            try
            {
                args = JsonConvert.DeserializeObject<List<string[]>>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                Logger.Log($"Error loading advanced arg JSON: {e.Message}");
                args = new List<string[]>();
            }

            foreach (string[] arg in args ?? new List<string[]>())
            {
                if (arg.Length >= 3)
                {
                    rows.Add(new EncoderArgRow(arg[0], arg[1], arg[2], arg.Length >= 4 ? arg[3] : "",
                        arg.Length >= 5 ? arg[4] : "", arg.Length >= 6 ? arg[5] : ""));
                }
            }

            return rows;
        }

        /// <summary> The encoder's documented parameters, with whatever was last typed into them. </summary>
        /// <param name="key">Where previously typed values are read back from, or null for a grid whose
        /// values are not kept - which is Quick Convert's, that tab persisting nothing. A null loses them
        /// across a switch between encoders as well as across a session, the saved store being the only
        /// thing that carried them: the rows are rebuilt from the encoder's JSON every time one is
        /// selected.</param>
        public static void Load(ObservableCollection<EncoderArgRow> target, IEncoder enc, string folder, Config.Key? key)
        {
            target.Clear();
            Dictionary<string, string> saved = null;

            if (key != null)
                ReadSaved(key.Value).TryGetValue(enc.Name, out saved);

            foreach (EncoderArgRow row in ReadRows(enc, folder))
            {
                if (saved != null && saved.TryGetValue(row.Argument, out string value))
                    row.Value = value;

                target.Add(row);
            }
        }

        /// <summary>
        /// Values typed into an advanced argument grid, kept per encoder. The rows themselves are
        /// rebuilt from the encoder's JSON every time it is selected, which is what used to throw
        /// the values away - on a restart and on every switch between encoders.
        /// </summary>
        public static void Save(IEnumerable<EncoderArgRow> rows, IEncoder enc, Config.Key key)
        {
            if (enc == null)
                return;

            Dictionary<string, Dictionary<string, string>> all = ReadSaved(key);
            Dictionary<string, string> filled = Filled(rows);

            // Blank rows are the normal state, and storing them would grow the config with nothing
            if (filled.Count > 0)
                all[enc.Name] = filled;
            else
                all.Remove(enc.Name);

            Config.Set(key, JsonConvert.SerializeObject(all));
        }

        /// <summary> The rows the user has actually filled in, by argument name. </summary>
        public static Dictionary<string, string> Filled(IEnumerable<EncoderArgRow> rows)
        {
            return rows
                .Where(r => r.Argument.IsNotEmpty() && r.Value.IsNotEmpty())
                .GroupBy(r => r.Argument.Trim())
                .ToDictionary(g => g.Key, g => g.Last().Value.Trim());
        }

        /// <summary>
        /// The filled-in rows as the standalone encoders' own "--key=value" arguments, which is what
        /// av1an passes on. Every row the user has not filled in is skipped: the grid is preloaded
        /// with an encoder's documented parameters so they can be read and edited in place, so most
        /// rows are blank most of the time, and passing those would put a valueless flag on the
        /// command line and fail the encode before it started.
        /// </summary>
        public static string BuildCli(IEnumerable<EncoderArgRow> rows)
        {
            return string.Join(" ", Filled(rows).Select(x => $"--{x.Key.TrimStart('-')}={x.Value}"));
        }

        /// <summary>
        /// The filled-in rows as bare "key=value" pairs, which is the form the ffmpeg encoders are
        /// handed and re-spell for themselves - as one ":"-joined parameter list for the four that
        /// have such an option, and as one AVOption apiece for the ones that do not. See
        /// <see cref="FfmpegEncoderArgs"/>.
        /// <para/>
        /// A value containing a space cannot survive this, the pairs being space-separated. That is
        /// the same limit <see cref="BuildCli"/> has always had, and no parameter either grid offers
        /// takes one.
        /// </summary>
        public static string BuildPairs(IEnumerable<EncoderArgRow> rows)
        {
            return string.Join(" ", Filled(rows).Select(x => $"{x.Key.TrimStart('-')}={x.Value}"));
        }

        private static Dictionary<string, Dictionary<string, string>> ReadSaved(Config.Key key)
        {
            try
            {
                string json = Config.Get(key);

                if (json.IsEmpty())
                    return new Dictionary<string, Dictionary<string, string>>();

                return JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(json)
                    ?? new Dictionary<string, Dictionary<string, string>>();
            }
            catch (Exception e)
            {
                // A hand-edited or truncated entry should cost the saved values, not the whole tab
                Logger.Log($"Failed to read saved encoder arguments: {e.Message}", true);
                return new Dictionary<string, Dictionary<string, string>>();
            }
        }
    }
}
