using System;
using System.Collections.Generic;
using System.Linq;

namespace Nmkoder.Data.Codecs
{
    /// <summary>
    /// A named set of advanced-grid argument values, chosen for one kind of source material.
    /// <para/>
    /// The advanced grid holds every parameter an encoder documents, blank, for the user to fill in one
    /// at a time - which requires knowing what forty-odd of them do before the first encode. A preset is
    /// the same grid filled in for a kind of content, so the starting point is a considered set rather
    /// than the encoder's defaults, and every value stays visible and editable afterwards.
    /// </summary>
    public class EncoderArgPreset
    {
        /// <summary> What the button says. </summary>
        public string Name { get; }

        /// <summary> What the source looks like and what the preset does about it, for the tooltip. </summary>
        public string Description { get; }

        /// <summary> Argument name (as it appears in the grid, without dashes) to the value to set. </summary>
        public IReadOnlyDictionary<string, string> Values { get; }

        public EncoderArgPreset(string name, string description, Dictionary<string, string> values)
        {
            Name = name;
            Description = description;
            Values = values;
        }

        /// <summary> Whether a grid's filled-in values are exactly this preset, so applying it would change nothing. </summary>
        public bool Matches(IReadOnlyDictionary<string, string> filled)
        {
            return filled.Count == Values.Count &&
                Values.All(v => filled.TryGetValue(v.Key, out string value) && value == v.Value);
        }

        /// <summary>
        /// Whether everything filled in came from this preset, allowing for some of it to be absent.
        /// A preset drops any argument the encoder in use does not have, so what lands in the grid is
        /// not always the whole of it - and what landed is still nobody's hand-typed work to protect.
        /// </summary>
        public bool Covers(IReadOnlyDictionary<string, string> filled)
        {
            return filled.Count > 0 &&
                filled.All(f => Values.TryGetValue(f.Key, out string value) && value == f.Value);
        }
    }
}
