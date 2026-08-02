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
    /// <para/>
    /// The box entries enlarge a source smaller than the target rather than clamping at its own size, so
    /// "2160p (4K)" means 3840x2160 for a 1080p file. Naming a target and being handed the source back is
    /// the more surprising of the two behaviours, and the readout and the encode log both say when a
    /// picture is being grown - what upscaling costs is worth stating, not worth refusing on someone's
    /// behalf. The cost is real: enlarging invents no detail and spends bitrate keeping the softness it
    /// produces, and the usual good reason to do it anyway is a platform whose bitrate ladder pays more
    /// for a larger frame. It also means a preset left set across a batch of mixed-resolution files
    /// enlarges the small ones, which is what the per-file log line is there to make visible.
    /// <para/>
    /// The percentage entries below take no part in this - they are proportions of whatever the source
    /// is, and all three shrink it. A percentage over 100 is somebody asking for an upscale outright,
    /// which ResizeConfig honours without consulting the flag at all.
    /// </summary>
    public class ResizePresets
    {
        public const string CustomKey = "custom";
        public const string NoneKey = "none";

        /// <summary> One "p" entry: a box to fit inside, which may enlarge the source. See the note on <see cref="All"/>. </summary>
        private static ResizeConfig Box(int w, int h, string key)
        {
            return ResizeConfig.FitBox(w, h, key, allowUpscale: true);
        }

        public static readonly List<ResizePreset> All = new List<ResizePreset>
        {
            new ResizePreset(NoneKey, "No resizing", () => new ResizeConfig()),
            new ResizePreset("2160p", "2160p (4K)", () => Box(3840, 2160, "2160p")),
            new ResizePreset("1440p", "1440p", () => Box(2560, 1440, "1440p")),
            new ResizePreset("1080p", "1080p (Full HD)", () => Box(1920, 1080, "1080p")),
            new ResizePreset("720p", "720p (HD)", () => Box(1280, 720, "720p")),
            new ResizePreset("576p", "576p", () => Box(1024, 576, "576p")),
            new ResizePreset("480p", "480p", () => Box(854, 480, "480p")),
            new ResizePreset("360p", "360p", () => Box(640, 360, "360p")),
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

        /// <summary>
        /// The entry to select for a configuration. A resize with no preset behind it - one migrated from
        /// the old scale boxes, or written by a build whose preset list has since changed - is a custom
        /// one rather than none at all: falling back to "No resizing" would show a resize being off while
        /// it was on, which is the one thing the readout must never say.
        /// </summary>
        public static int IndexFor(ResizeConfig cfg)
        {
            if (cfg == null || cfg.Mode == ResizeMode.Disabled)
                return IndexOf(NoneKey);

            int i = All.FindIndex(p => p.Key == (cfg.PresetKey ?? ""));
            return i < 0 ? IndexOf(CustomKey) : i;
        }

        public static ResizePreset Get(int index)
        {
            return index >= 0 && index < All.Count ? All[index] : All.First();
        }

        /// <summary>
        /// A saved resize as this build defines it: an entry from the list is rebuilt from its key, and
        /// only a hand-configured one is restored field by field.
        /// <para/>
        /// The whole config is serialised, so without this a preset means whatever it meant on the day it
        /// was picked. That is the same shape of bug as a default that only applies on a first run: the
        /// upscaling the box entries now do would have reached nobody who already had one selected, and
        /// their 2160p would have gone on quietly handing back a 1080p source - the exact behaviour the
        /// change is undoing - with nothing on screen to say why it differed from a new install's.
        /// <para/>
        /// Safe because a preset's fields are not editable: the Configure… button only appears for the
        /// Custom entry, and the dialog stamps <see cref="CustomKey"/> on whatever comes out of it. So
        /// there is nothing of the user's to overwrite here, and the definition in this file is the only
        /// place a box target is stated.
        /// </summary>
        public static ResizeConfig Restore(ResizeConfig saved)
        {
            if (saved == null)
                return new ResizeConfig();

            string key = saved.PresetKey ?? "";

            if (key == CustomKey || key.Length < 1)
                return saved;

            ResizePreset preset = All.FirstOrDefault(p => p.Key == key);
            return preset == null ? saved : preset.Build();
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
