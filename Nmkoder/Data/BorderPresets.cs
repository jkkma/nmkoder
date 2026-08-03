using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data
{
    /// <summary> One entry of the borders dropdown: a name, and the target ratio it stands for. </summary>
    public class BorderPreset
    {
        /// <summary> Stable across releases and across reorderings of the list, so a configured target
        /// can be matched back to the entry that produced it. </summary>
        public string Key { get; }
        public string Name { get; }
        public int RatioWidth { get; }
        public int RatioHeight { get; }

        public BorderPreset(string key, string name, int ratioWidth = 0, int ratioHeight = 0)
        {
            Key = key;
            Name = name;
            RatioWidth = ratioWidth;
            RatioHeight = ratioHeight;
        }

        public BorderConfig Build()
        {
            return new BorderConfig(RatioWidth, RatioHeight, Key);
        }
    }

    /// <summary>
    /// The borders dropdown's contents: the target shapes worth padding to, and nothing else.
    /// <para/>
    /// Every entry is a *ratio* rather than a frame size, which is what lets one of them be the right
    /// answer for a 2.39:1 film and a 4:3 capture at once - and what keeps the list this short. A
    /// target frame size is already available on the other row: an exact resize with "Letterbox with
    /// black bars" pads to a named WxH, and scales to reach it. This does not scale at all.
    /// <para/>
    /// The Quick Convert box saves its selected index, so entries may be appended to this list but not
    /// reordered - the same rule the deinterlace mode list is under. The AV1AN box saves nothing, its
    /// whole tab starting each session at the defaults.
    /// </summary>
    public class BorderPresets
    {
        public const string NoneKey = "none";

        public static readonly List<BorderPreset> All = new List<BorderPreset>
        {
            new BorderPreset(NoneKey, "No borders"),
            new BorderPreset("16:9", "16:9 (Widescreen)", 16, 9),
            new BorderPreset("4:3", "4:3 (Standard)", 4, 3),
            new BorderPreset("1:1", "1:1 (Square)", 1, 1),
            new BorderPreset("9:16", "9:16 (Vertical)", 9, 16),
            // 64:27 rather than a literal 21:9, which is not a ratio any display is built to - it is
            // how 2.370:1 is spoken about, and it is the value the ratio table matches "21:9" on.
            new BorderPreset("21:9", "21:9 (Ultrawide)", 64, 27),
        };

        public static int IndexOf(string key)
        {
            int i = All.FindIndex(p => p.Key == (key ?? ""));
            return i < 0 ? 0 : i;
        }

        /// <summary> The entry to select for a configuration. </summary>
        public static int IndexFor(BorderConfig cfg)
        {
            if (cfg == null || !cfg.IsSet)
                return IndexOf(NoneKey);

            int i = All.FindIndex(p => p.Key == (cfg.PresetKey ?? ""));

            // Matched on the ratio itself where the key is not one this build has. Not reachable
            // today - every configuration here is built from an entry of this list, there being no
            // dialog to make one by hand - but falling back to "No borders" would show borders as
            // off while they ran, which is the one thing a readout must never say.
            if (i < 0)
                i = All.FindIndex(p => p.RatioWidth > 0 && p.RatioWidth * cfg.RatioHeight == p.RatioHeight * cfg.RatioWidth);

            return i < 0 ? IndexOf(NoneKey) : i;
        }

        public static BorderPreset Get(int index)
        {
            return index >= 0 && index < All.Count ? All[index] : All.First();
        }
    }
}
