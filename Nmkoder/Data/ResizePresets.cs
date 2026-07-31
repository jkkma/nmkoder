using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary> One entry of the resize dropdown: a name, and the resize it stands for. </summary>
    public class ResizePreset
    {
        /// <summary> Stable across releases and across reorderings of the list, because it is what gets saved. </summary>
        public string Key { get; }
        public string Name { get; }

        private readonly System.Func<ResizeConfig> _build;

        public ResizePreset(string key, string name, System.Func<ResizeConfig> build)
        {
            Key = key;
            Name = name;
            _build = build;
        }

        public ResizeConfig Build()
        {
            ResizeConfig cfg = _build();
            cfg.PresetKey = Key;
            return cfg;
        }
    }

    /// <summary>
    /// The resize dropdown's contents.
    /// <para/>
    /// Every "p" entry is a *box* the picture is fitted inside rather than a height it is forced to,
    /// which is the whole point of the tool: 1080p means 1920x1080 for a 16:9 source, 1920x804 for a
    /// 2.39:1 film, 1440x1080 for a 4:3 DVD and 1080x1920 for a phone video, and the user picks one
    /// entry rather than working out four different pairs of numbers. A user who really does want the
    /// height forced - scope content at a full 1080 lines, so 2582x1080 - says so in the dialog, which
    /// is the mode the box entries deliberately are not.
    /// </summary>
    public class ResizePresets
    {
        public const string CustomKey = "custom";
        public const string NoneKey = "none";

        public static readonly List<ResizePreset> All = new List<ResizePreset>
        {
            new ResizePreset(NoneKey, "No resizing", () => new ResizeConfig()),
            new ResizePreset("2160p", "2160p (4K)", () => ResizeConfig.FitBox(3840, 2160, "2160p")),
            new ResizePreset("1440p", "1440p", () => ResizeConfig.FitBox(2560, 1440, "1440p")),
            new ResizePreset("1080p", "1080p (Full HD)", () => ResizeConfig.FitBox(1920, 1080, "1080p")),
            new ResizePreset("720p", "720p (HD)", () => ResizeConfig.FitBox(1280, 720, "720p")),
            new ResizePreset("576p", "576p", () => ResizeConfig.FitBox(1024, 576, "576p")),
            new ResizePreset("480p", "480p", () => ResizeConfig.FitBox(854, 480, "480p")),
            new ResizePreset("360p", "360p", () => ResizeConfig.FitBox(640, 360, "360p")),
            new ResizePreset("75pc", "75% of the source", () => ResizeConfig.Proportion(75, "75pc")),
            new ResizePreset("50pc", "50% of the source", () => ResizeConfig.Proportion(50, "50pc")),
            new ResizePreset("25pc", "25% of the source", () => ResizeConfig.Proportion(25, "25pc")),
            new ResizePreset(CustomKey, "Custom…", () => new ResizeConfig { Mode = ResizeMode.Fit }),
        };

        public static int IndexOf(string key)
        {
            int i = All.FindIndex(p => p.Key == (key ?? ""));
            return i < 0 ? 0 : i;
        }

        public static ResizePreset Get(int index)
        {
            return index >= 0 && index < All.Count ? All[index] : All.First();
        }

        /// <summary>
        /// What the entry reads as for a given source - "1080p (Full HD) — 1920x804" - so the list says
        /// what each target will actually produce for the file that is loaded, rather than naming a
        /// resolution the source may have nothing to do with. With no file loaded there is nothing to
        /// compute against, and the entry falls back to naming its own box.
        /// <para/>
        /// Deliberately a function of the source alone and not of what is currently configured: the label
        /// of the Custom entry would otherwise have to be rewritten every time the selection moved, and
        /// rewriting a dropdown's items from inside its own SelectionChanged is not allowed. What Custom
        /// is set to is on the line under the box instead, which is where the result belongs anyway.
        /// </summary>
        public static string GetLabel(ResizePreset preset, Size storage, Size sar)
        {
            if (preset.Key == NoneKey || preset.Key == CustomKey)
                return preset.Name;

            ResizeConfig cfg = preset.Build();

            if (storage.IsEmpty)
                return cfg.Mode == ResizeMode.Fit ? $"{preset.Name} — fits {cfg.TargetWidth}x{cfg.TargetHeight}" : preset.Name;

            Size result = cfg.Compute(storage, sar);

            if (result.IsEmpty)
                return preset.Name;

            if (result == storage)
                return $"{preset.Name} — no change";

            return $"{preset.Name} — {result.Width}x{result.Height}";
        }
    }
}
