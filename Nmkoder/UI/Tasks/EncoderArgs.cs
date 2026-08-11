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
        /// <summary>
        /// Where both sets are filed, under <see cref="Paths.GetBinPath"/>. One level of its own
        /// rather than a folder per tool directly under bin/, and that is not tidiness: bin/ is what
        /// goes on the launched tool's PATH, so a folder there occupies a name a *binary* wants. The
        /// ffmpeg list used to live in bin/ffmpeg/, which is exactly where bundle-tools.sh installs
        /// the ffmpeg binary - so the copy failed and every linux-x64 release from 2.8.25 to 2.8.43
        /// shipped without it. "encoderArgs" is not a tool and never will be.
        /// </summary>
        public const string ArgsFolder = "encoderArgs";

        /// <summary> Where the AV1AN tab's lists are filed, under <see cref="ArgsFolder"/>. </summary>
        public const string Av1anFolder = "av1an";

        /// <summary> Where Quick Convert's lists are filed, under <see cref="ArgsFolder"/>. </summary>
        public const string FfmpegFolder = "ffmpeg";

        /// <summary>
        /// Which folder an encoder's list lives in - the tab's own, unless the encoder is a binary this
        /// app launches itself, in which case it is av1an's whichever tab is asking.
        /// <para/>
        /// The folder follows the *encoder* rather than the tab because Quick Convert now drives both
        /// kinds: the standalone binaries take the CLI parameters av1an's lists name, while NVENC, GIF,
        /// PNG and JPEG are still ffmpeg's own. The CRF ladder is unaffected - it runs ffmpeg encoders
        /// deliberately, and none of them is an <see cref="Data.Codecs.Video.IBinaryEncoder"/>.
        /// </summary>
        public static string FolderFor(IEncoder enc, string tabDefault)
        {
            return enc is Data.Codecs.Video.IBinaryEncoder ? Av1anFolder : tabDefault;
        }

        /// <summary> The list file's name, which is the class name except for the direct encoders -
        /// <c>DirectX264</c> reads <c>X264.json</c>, the list already written for that binary. </summary>
        public static string ListNameFor(IEncoder enc)
        {
            return (enc as Data.Codecs.Video.IBinaryEncoder)?.ArgListName ?? enc.Name;
        }

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
            string jsonPath = Path.Combine(Paths.GetBinPath(), ArgsFolder, FolderFor(enc, folder), ListNameFor(enc) + ".json");

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

        /// <summary>
        /// The encoder's documented parameters, blank.
        /// <para/>
        /// **Neither tab keeps what is typed into its grid**, so there is nothing to read back and no key
        /// to read it from. The rows are rebuilt from the encoder's JSON every time an encoder is
        /// selected, and a saved store used to carry the typed values across that rebuild - which is what
        /// made them survive both an encoder switch and a restart. Both go together: an advanced argument
        /// describes the encode in front of you, and one left over from another source is expensive to
        /// have applied and easy not to notice.
        /// <para/>
        /// The reading and writing were deleted rather than left unused behind a null key. A store that is
        /// still written and no longer read is precisely the thing somebody wires back up later, on the
        /// reasonable-looking grounds that the values are already there. Existing config files still carry
        /// the Av1anEncoderArgs and EncEncoderArgs entries; nothing reads them.
        /// </summary>
        public static void Load(ObservableCollection<EncoderArgRow> target, IEncoder enc, string folder)
        {
            target.Clear();

            foreach (EncoderArgRow row in ReadRows(enc, folder))
                target.Add(row);
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

    }
}
