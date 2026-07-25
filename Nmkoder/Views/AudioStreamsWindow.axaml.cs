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

        public static async Task<List<AudioConfigurationEntry>> Show(MediaFile current, int baseBitrate)
        {
            var window = new AudioStreamsWindow();
            window.Populate(current, baseBitrate);

            Window owner = UiUtils.MainWindowHandle;

            if (owner != null && owner.IsVisible)
                await window.ShowDialog(owner);
            else
                window.Show();

            return window.ConfigurationEntries;
        }

        private void Populate(MediaFile current, int baseBitrate)
        {
            List<AudioStream> audStreams = TrackList.Items
                .Where(x => x.Stream.Type == Stream.StreamType.Audio)
                .Select(x => (AudioStream)x.Stream)
                .ToList();

            List<AudioConfigurationEntry> currentEntries = TrackList.currentAudioConfig?.GetConfig(current);

            for (int i = 0; i < audStreams.Count; i++)
            {
                AudioStream s = audStreams[i];
                int br = (baseBitrate * MiscUtils.GetAudioBitrateMultiplier(s.Channels)).RoundToInt();
                string title = string.IsNullOrWhiteSpace(s.Title) ? "None" : s.Title.Trunc(35);

                bool hasSaved = currentEntries != null && i < currentEntries.Count;
                int channels = hasSaved ? currentEntries[i].ChannelCount : s.Channels;
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
