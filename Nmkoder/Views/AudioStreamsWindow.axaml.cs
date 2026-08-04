using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.UI;
using Nmkoder.Utils;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.Views
{
    /// <summary> Per-track audio channel/bitrate configuration. </summary>
    public partial class AudioStreamsWindow : Window
    {
        /// <summary> Confirmed configuration, or null when the dialog was dismissed. </summary>
        public List<AudioConfigurationEntry> ConfigurationEntries { get; private set; }

        private readonly ObservableCollection<AudioTrackRow> _rows = new ObservableCollection<AudioTrackRow>();

        public AudioStreamsWindow()
        {
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {
            Grid.ItemsSource = _rows;
        }

        /// <param name="overrideChannels"> What the Audio tab's Channels dropdown asks for, or 0 for
        /// "keep original". The rows start from it rather than from each source track's own layout: this
        /// dialog's numbers replace that dropdown outright once it is confirmed - every row is written
        /// into the configuration whether it was edited or not - so seeding from the source silently
        /// undid a downmix the user had already picked. It is opened the moment the mode is switched to
        /// per-track, so that costs nothing more than a trip through a dialog nobody asked for. </param>
        public static async Task<List<AudioConfigurationEntry>> Show(MediaFile current, int baseBitrate, int overrideChannels)
        {
            var window = new AudioStreamsWindow();
            window.Populate(current, baseBitrate, overrideChannels);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.ConfigurationEntries;
        }

        private void Populate(MediaFile current, int baseBitrate, int overrideChannels)
        {
            List<AudioStream> audStreams = TrackList.Items
                .Where(x => x.Stream.Type == Stream.StreamType.Audio)
                .Select(x => (AudioStream)x.Stream)
                .ToList();

            List<AudioConfigurationEntry> currentEntries = TrackList.currentAudioConfig?.GetConfig(current);

            for (int i = 0; i < audStreams.Count; i++)
            {
                AudioStream s = audStreams[i];
                // The layout this track is headed for, which is the dropdown's where it names one - and
                // the bitrate is scaled to that same number, since a count and a bitrate picked for
                // different layouts describe no track at all.
                int startChannels = overrideChannels > 0 ? overrideChannels : s.Channels;
                int br = (baseBitrate * MiscUtils.GetAudioBitrateMultiplier(startChannels)).RoundToInt();
                string title = string.IsNullOrWhiteSpace(s.Title) ? "None" : s.Title.Trunc(35);

                bool hasSaved = currentEntries != null && i < currentEntries.Count;
                int channels = hasSaved ? currentEntries[i].ChannelCount : startChannels;
                int kbps = hasSaved ? currentEntries[i].BitrateKbps : br;

                _rows.Add(new AudioTrackRow($"#{i + 1}", title, s.Language.ToUpper(), channels, kbps));
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ConfigurationEntries = _rows
                .Select((row, i) => new AudioConfigurationEntry(i, row.Channels.Clamp(1, 24), row.BitrateKbps.Clamp(8, 8192)))
                .ToList();

            Close();
        }
    }
}
