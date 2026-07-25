using Avalonia.Controls;
using Avalonia.Interactivity;
using Nmkoder.Data.Streams;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.Media;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Stream = Nmkoder.Data.Streams.Stream;

namespace Nmkoder.Views
{
    partial class MainWindow
    {
        /// <summary> Suppresses the metadata/default-track refresh while doing bulk check changes. </summary>
        public bool ignoreStreamListCheck;

        public void RefreshStreamListUi()
        {
            string note = "Manual track selection is not available in Batch Processing Mode.";

            if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
                FormatInfoLabel.Text = note;
            else if (FormatInfoLabel.Text == note)
                FormatInfoLabel.Text = "";
        }

        private void StreamList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTrackListBtnsState();

            if (StreamListBox.SelectedItem is not StreamListEntry entry)
            {
                StreamDetailsBox.Text = "";
                return;
            }

            StreamDetailsBox.Text = TrackList.GetStreamDetails(entry.Stream, entry.MediaFile);
        }

        public void UpdateTrackListBtnsState()
        {
            bool oneSelected = StreamListBox.SelectedItems?.Count == 1;
            TrackListMoveUpBtn.IsVisible = oneSelected;
            TrackListMoveDownBtn.IsVisible = oneSelected;

            TrackListExtractTracksBtn.IsVisible = StreamListBox.SelectedItem is StreamListEntry entry
                && entry.Stream.Type == Stream.StreamType.Attachment;
        }

        private void OnStreamCheckedChanged()
        {
            if (ignoreStreamListCheck)
                return;

            OnCheckedStreamsChange();
        }

        public void OnCheckedStreamsChange()
        {
            UpdateDefaultStreamsUi();
            QuickConvertUi.LoadMetadataGrid();
        }

        public void UpdateDefaultStreamsUi()
        {
            var checkedEntries = TrackList.CheckedItems.ToList();
            List<StreamListEntry> a = checkedEntries.Where(x => x.Stream.Type == Stream.StreamType.Audio).ToList();
            List<StreamListEntry> s = checkedEntries.Where(x => x.Stream.Type == Stream.StreamType.Subtitle).ToList();

            TrackListDefaultAudioBox.IsEnabled = a.Count > 0;
            TrackListDefaultSubsBox.IsEnabled = s.Count > 0;

            bool zeroIdx = Config.GetBool(Config.Key.UseZeroIndexedStreams);

            // Audio
            var audioItems = new List<object>();

            for (int i = 0; i < a.Count; i++)
            {
                var stream = (AudioStream)a[i].Stream;
                var parts = new List<string>
                {
                    $"#{(zeroIdx ? i : i + 1).ToString().PadLeft(2, '0')}",
                    stream.Language.ToUpper().Trunc(6),
                    stream.Title.Trunc(22)
                };
                audioItems.Add(string.Join(" - ", parts.Where(x => !string.IsNullOrWhiteSpace(x))));
            }

            int defaultAudioIdx = a.FindIndex(x => x.Stream.IsDefault);
            TrackListDefaultAudioBox.SetItems(audioItems, a.Count > 0 ? Math.Max(0, defaultAudioIdx) : -1);

            // Subtitles ("None" occupies index 0, so stream indices are offset by one)
            var subItems = new List<object> { "None" };

            for (int i = 0; i < s.Count; i++)
            {
                var stream = (SubtitleStream)s[i].Stream;
                var parts = new List<string>
                {
                    $"#{(zeroIdx ? i : i + 1).ToString().PadLeft(2, '0')}",
                    stream.Language.ToUpper().Trunc(6),
                    stream.Title.Trunc(18)
                };
                subItems.Add($"{string.Join(" - ", parts.Where(x => !string.IsNullOrWhiteSpace(x)))} ({Aliases.GetNicerCodecName(stream.Codec).Trunc(10)})");
            }

            int defaultSubIdx = s.FindIndex(x => x.Stream.IsDefault);
            TrackListDefaultSubsBox.SetItems(subItems, s.Count > 0 ? (defaultSubIdx >= 0 ? defaultSubIdx + 1 : 1) : 0);
        }

        #region Move Buttons

        private void TrackListMoveUp_Click(object sender, RoutedEventArgs e)
        {
            UiUtils.MoveItem(TrackList.Items, StreamListBox.SelectedItem as StreamListEntry, UiUtils.MoveDirection.Up);
        }

        private void TrackListMoveDown_Click(object sender, RoutedEventArgs e)
        {
            UiUtils.MoveItem(TrackList.Items, StreamListBox.SelectedItem as StreamListEntry, UiUtils.MoveDirection.Down);
        }

        private async void TrackListExtract_Click(object sender, RoutedEventArgs e)
        {
            await TrackList.Extract(StreamListBox.SelectedItem as StreamListEntry);
        }

        #endregion

        #region Auto-Check

        private void TrackListCheckTracks_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            void Add(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (s, args) => action();
                menu.Items.Add(item);
            }

            Add("Check All", () => TrackList.CheckAll(true));
            Add("Check None", () => TrackList.CheckAll(false));
            Add("Invert Selection", TrackList.InvertSelection);
            Add("All Video Tracks", () => TrackList.CheckTracksOfType(Stream.StreamType.Video));
            Add("All Audio Tracks", () => TrackList.CheckTracksOfType(Stream.StreamType.Audio));
            Add("All Subtitle Tracks", () => TrackList.CheckTracksOfType(Stream.StreamType.Subtitle));
            Add("First Track Of Each Type", TrackList.CheckFirstOfEachType);
            Add("First Track Of Each Language Per Type", TrackList.CheckFirstOfEachLangOfEachType);

            menu.Open(TrackListCheckTracksBtn);
        }

        #endregion

        #region Sort

        private void TrackListSortTracks_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            void Add(string header, TrackList.TrackSort sort, bool reverse)
            {
                var item = new MenuItem { Header = header };
                item.Click += (s, args) => TrackList.SortTracks(sort, reverse);
                menu.Items.Add(item);
            }

            Add("Language (A-Z)", TrackList.TrackSort.Language, false);
            Add("Language (Z-A)", TrackList.TrackSort.Language, true);
            Add("Title (A-Z)", TrackList.TrackSort.Title, false);
            Add("Title (Z-A)", TrackList.TrackSort.Title, true);
            Add("Codec (A-Z)", TrackList.TrackSort.Codec, false);
            Add("Codec (Z-A)", TrackList.TrackSort.Codec, true);

            menu.Open(TrackListSortTracksBtn);
        }

        #endregion
    }
}
