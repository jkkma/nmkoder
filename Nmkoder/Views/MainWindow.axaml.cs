using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using Nmkoder.IO;
using Nmkoder.Main;
using Nmkoder.OS;
using Nmkoder.UI;
using Nmkoder.UI.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Nmkoder.Views
{
    public partial class MainWindow : Window
    {
        private bool _initialized;

        /// <summary> Rows of the metadata grid, bound in XAML. </summary>
        public ObservableCollection<MetadataRow> MetadataRows { get; } = new ObservableCollection<MetadataRow>();

        /// <summary> Custom ffmpeg video filters. </summary>
        public ObservableCollection<FilterRow> EncFilterRows { get; } = new ObservableCollection<FilterRow>();

        /// <summary> Custom av1an video filters. </summary>
        public ObservableCollection<FilterRow> Av1anFilterRows { get; } = new ObservableCollection<FilterRow>();

        /// <summary> Per-encoder advanced av1an arguments. </summary>
        public ObservableCollection<EncoderArgRow> Av1anArgRows { get; } = new ObservableCollection<EncoderArgRow>();

        public RunTask.TaskType RunningTask = RunTask.TaskType.None;

        public MainWindow()
        {
            Program.MainWin = this;
            InitializeComponent();
            SetupUi();
        }

        private void SetupUi()
        {

            Logger.textbox = LogBox;
            Notifications.Attach(this);

            FileListBox.ItemsSource = FileList.Items;
            StreamListBox.ItemsSource = TrackList.Items;
            EncMetadataGrid.ItemsSource = MetadataRows;
            EncAdvancedFiltersGrid.ItemsSource = EncFilterRows;
            Av1anAdvancedFiltersGrid.ItemsSource = Av1anFilterRows;
            // Av1anArgRows has no grid of its own: the category tabs each show a slice of it,
            // built in LoadAv1anArgCategoryTabs whenever the rows are reloaded.
            SetThumbnail(AppImages.Placeholder, "");

            ListEntryBase.CheckedChanged += (s, e) => OnStreamCheckedChanged();

            SetUpDragDrop();
            SetUpModifierTracking();
            RestoreLayout();

            Opened += OnOpened;
            Closing += OnClosing;
        }

        #region Startup / Shutdown

        private async void OnOpened(object sender, EventArgs e)
        {
            InitQuickConvert();
            InitAv1an();

            QuickConvert.Init();
            Av1an.Init();
            LoadUiConfig();

            // The SelectionChanged handlers bail out until _initialized is set, so the initial
            // encoder-dependent UI state has to be applied explicitly here.
            QuickConvertUi.VidEncoderSelected(EncVidCodecsBox.SelectedIndex);
            QuickConvertUi.AudEncoderSelected(EncAudCodecBox.SelectedIndex);
            Av1anUi.VidEncoderSelected(Av1anCodecBox.SelectedIndex);
            Av1anUi.AudEncoderSelected(Av1anAudCodecBox.SelectedIndex);

            // ...and the saved encode settings over the top of it. Selecting an encoder fills the
            // quality, preset and colour boxes with that encoder's own defaults, so this is the
            // earliest point at which restoring them is not immediately undone.
            LoadQuickConvertSettings();
            LoadAv1anEncodeSettings();

            await RefreshFileListUi();

            var packageArg = Program.args.FirstOrDefault(x => x.StartsWith("package="));

            if (packageArg != null)
                await PackageBuild.Run(packageArg.Split('=')[1]);

            if (Nmkoder.Data.Paths.GetExe().Length > 150)
                Logger.Log($"Warning: Nmkoder's installation path is very long ({Nmkoder.Data.Paths.GetExe().Length} characters) - This can lead to problems. It is recommended to move it to a higher directory to reduce the path length.");

            UpdateResetSettingsText();
            QuickConvertUi.InitFile();
            Av1anUi.RefreshResumeButton(logIfAny: true); // An encode interrupted before a restart is otherwise never mentioned again

            RefreshRecentFilesButton();

            _initialized = true;

            // Whichever tab the last session was left on was selected before _initialized was set,
            // so its SelectionChanged did nothing - and the File List tab, which the XAML selects,
            // never raises one at all.
            await ApplySelectedTab();

            if (Program.fileArgs.Length > 0)
                await FileList.HandleFiles(Program.fileArgs, true);
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            SaveOnClose();

            // Holding Shift while closing leaves subprocesses (e.g. av1an) running.
            if (!Hotkeys.ShiftHeld)
                ProcessManager.KillAll();
            else
                SuspendResume.ResumeIfPaused(); // "Left running" must not mean "left frozen forever"

            Program.Cleanup();
        }

        /// <summary>
        /// Saves what the session changed, and never throws doing it. Every group in here reads the
        /// task UI, which a startup that went wrong can leave half-built, while what runs after it
        /// stops the encodes this process started - so an exception getting out would close the
        /// window, end the process, and leave an av1an running with nothing left able to stop it.
        /// Settings are worth saving; they are not worth that.
        ///
        /// Guarded a group at a time as well, so one of them failing does not cost the rest theirs,
        /// and all of it shares a batch so it is still a single write.
        /// </summary>
        private void SaveOnClose()
        {
            try
            {
                using (Config.Batch())
                {
                    // Layout first: it is the one thing here that is true even when startup never
                    // got far enough to fill the task UI in.
                    TrySave(SaveLayout, "window layout");
                    TrySave(SaveUiConfig, "selected codecs");
                    TrySave(SaveQuickConvertSettings, "Quick Convert settings");
                    TrySave(SaveConfigAv1an, "AV1AN options");
                    TrySave(SaveAv1anEncodeSettings, "AV1AN encode settings");
                    TrySave(SaveAv1anAdvancedArgs, "AV1AN encoder arguments");
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to save settings while closing: {e.Message}", true);
            }
        }

        private static void TrySave(Action save, string what)
        {
            try
            {
                save();
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to save the {what}: {e.Message}", true);
            }
        }

        void LoadUiConfig()
        {
            // Batched because reading a key that is not in the file yet writes its default back, so on a
            // first run this whole method is a long series of writes rather than a series of reads.
            using (Config.Batch())
            {
                ConfigParser.LoadComboxIndex(FileListModeBox);
                // Quick Convert
                ConfigParser.LoadGuiElement(FfmpegContainerBox);
                ConfigParser.LoadComboxIndex(EncVidCodecsBox);
                ConfigParser.LoadComboxIndex(EncAudCodecBox);
                ConfigParser.LoadComboxIndex(EncSubCodecBox);
                ConfigParser.LoadComboxIndex(EncMetaCopySource);

                LoadConfigAv1an();
                LoadGeneralSettings();
                ResetSettingsOnNewFile.Load();
            }
        }

        public void SaveUiConfig()
        {
            if (!_initialized)
                return;

            using (Config.Batch())
            {
                ConfigParser.SaveComboxIndex(FileListModeBox);
                // Quick Convert
                ConfigParser.SaveGuiElement(FfmpegContainerBox);
                ConfigParser.SaveComboxIndex(EncVidCodecsBox);
                ConfigParser.SaveComboxIndex(EncAudCodecBox);
                ConfigParser.SaveComboxIndex(EncSubCodecBox);
                ConfigParser.SaveComboxIndex(EncMetaCopySource);
            }
        }

        #endregion

        #region Public control surface used by the task layer

        public TextBox FfmpegOutputBox => OutputPathBox;
        public TextBox CustomArgsInBox => EncCustomArgsIn;
        public TextBox CustomArgsOutBox => EncCustomArgsOut;
        public ComboBox Av1anColorsBox => Av1anColorSpaceBox;

        /// <summary>
        /// The top-level tabs, in the order they are declared in XAML. Anything keyed off the
        /// selected tab goes through this, so reordering the tabs means reordering this and
        /// nothing else.
        /// </summary>
        private enum MainTab { FileList, TrackList, Av1an, QuickConvert, Utilities, Settings }

        /// <summary> Index of the selected top-level tab (0 = File List, 1 = Track List, ...). </summary>
        public int SelectedMainTab
        {
            get => MainTabs.SelectedIndex;
            set => Dispatcher.UIThread.Post(() => MainTabs.SelectedIndex = value);
        }

        public bool IsInFocus() => IsActive;

        /// <summary>
        /// The File List and Track List tabs each have a preview panel, and both show the
        /// thumbnail of the same loaded file, so they are always written together.
        /// </summary>
        public void SetThumbnail(Bitmap image, string label)
        {
            ThumbnailBox.Source = image;
            ThumbLabel.Text = label;
            TrackThumbnailBox.Source = image;
            TrackThumbLabel.Text = label;
        }

        /// <summary>
        /// Fills the stream details pane, which is only worth its height when a track is
        /// actually selected - the rest of the time the list above it can have the space.
        /// </summary>
        public void SetStreamDetails(string details)
        {
            StreamDetailsBox.Text = details;
            StreamDetailsBox.IsVisible = !string.IsNullOrWhiteSpace(details);
        }

        public RunTask.TaskType SelectedTask
        {
            get
            {
                switch ((MainTab)MainTabs.SelectedIndex)
                {
                    case MainTab.QuickConvert: return RunTask.TaskType.Convert;
                    case MainTab.Av1an: return RunTask.TaskType.Av1an;
                    case MainTab.Utilities: return GetUtilsTaskType();
                    default: return RunTask.TaskType.None;
                }
            }
        }

        #endregion

        #region Progress / busy state

        public void SetProgress(int percent, bool ignoreIfNotBusy = true)
        {
            if (ignoreIfNotBusy && !Program.busy)
                return;

            RunOnUi(() => ProgBar.Value = percent.Clamp(0, 100));
        }

        /// <summary> silent skips the log: live progress lines arrive several times a second and
        /// their content is already being logged by whoever parsed it. </summary>
        public void SetStatus(string str, bool silent = false)
        {
            if (!silent)
                Logger.Log(str, true);

            RunOnUi(() => CurrentActionLabel.Text = str);
        }

        public void SetWorking(bool state, bool allowCancel = true)
        {
            Logger.Log($"SetWorking({state})", true);
            SetProgress(0, false);

            // Pause is offered exactly while Stop is, and a task that ended in any way - finished,
            // failed or canceled - must leave nothing frozen behind.
            if (state)
                SuspendResume.SetRunning(allowCancel);
            else
                SuspendResume.Reset();

            RunOnUi(() =>
            {
                RunBtn.IsVisible = !state;

                // Coming back from a task, whether Run is usable again depends on the tab that
                // happens to be open - the user is free to have switched during the encode.
                if (state)
                    RunBtn.IsEnabled = false;
                else
                    UpdateRunButtonState();
                StopBtn.IsVisible = state && allowCancel;

                if (RunTask.currentFileListMode == RunTask.FileListMode.Batch)
                {
                    TrackListDefaultAudioBox.IsEnabled = !state;
                    TrackListDefaultSubsBox.IsEnabled = !state;
                    TrackListCheckTracksBtn.IsVisible = !state;
                    TrackListSortTracksBtn.IsVisible = !state;
                    FileListMoveUpBtn.IsVisible = !state;
                    FileListMoveDownBtn.IsVisible = !state;
                    StreamListBlockPanel.IsVisible = state;
                }
            });

            Program.busy = state;
        }

        public void SetPauseButtonVisible(bool visible)
        {
            RunOnUi(() => PauseBtn.IsVisible = visible);
        }

        public void SetPauseButtonPaused(bool paused)
        {
            RunOnUi(() => PauseBtn.Content = paused ? "Resume" : "Pause");
        }

        /// <summary> Runs an action on the UI thread, whichever thread the caller is on. </summary>
        private static void RunOnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }

        #endregion

        #region Drag & Drop

        private void SetUpDragDrop()
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = Program.busy ? DragDropEffects.None : DragDropEffects.Copy;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (Program.busy)
                return;

            var items = e.DataTransfer.TryGetFiles();

            if (items == null || items.Length < 1)
                return;

            string[] files = items.Select(x => x.TryGetLocalPath()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            await ImportFiles(files);
        }

        /// <summary> Shared entry point for drag & drop and the "Add Files" buttons. </summary>
        public async Task ImportFiles(string[] files)
        {
            List<string> invalidFiles = files.Where(x => x.Length > 256).ToList();

            if (invalidFiles.Any())
            {
                string list = string.Join("\n", invalidFiles.Select(x => $"{x} ({x.Length} Chars)"));
                await UiUtils.ShowMessageBox($"The following files can not be imported because their path is too long:\n\n{list}\n\n" +
                    $"Please ensure their path is less than 256 characters long.", UiUtils.MessageType.Warning);
            }

            files = files.Where(x => x.Length <= 256).ToArray();

            if (files.Length < 1)
                return;

            if (FileList.Items.Count > 0)
            {
                FileImportWindow window = await FileImportWindow.Show(files, true);

                if (window.ImportFiles.Count > 0)
                    await FileList.HandleFiles(window.ImportFiles.ToArray(), window.Clear);
            }
            else
            {
                await FileList.HandleFiles(files, false);
            }
        }

        #endregion

        #region Modifier tracking

        /// <summary>
        /// Avalonia only reports modifiers through input events, so keep the last known state around
        /// for the "hold Shift" shortcuts.
        /// </summary>
        private void SetUpModifierTracking()
        {
            AddHandler(KeyDownEvent, (s, e) => Hotkeys.Update(e.KeyModifiers), RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => Hotkeys.Update(e.KeyModifiers), RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, (s, e) => Hotkeys.Update(e.KeyModifiers), RoutingStrategies.Tunnel);
        }

        #endregion

        #region Run controls

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            RunBtnClick();
        }

        public void RunBtnClick()
        {
            if (RunTask.currentFileListMode == RunTask.FileListMode.Mux)
                _ = RunTask.Start();
            else
                _ = RunTask.StartBatch();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            RunTask.canceledManually = true; // Distinguishes this from a task stopping itself, which is not the user's decision to review
            RunTask.Cancel("Canceled manually.", true);
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            Task.Run(SuspendResume.TogglePause);
        }

        /// <summary> Mirrored into RunTask because IsChecked can only be read on the UI thread.
        /// Deliberately not persisted - see RunTask.shutdownWhenDone. </summary>
        private void ShutdownWhenDone_Changed(object sender, RoutedEventArgs e)
        {
            bool armed = ShutdownWhenDoneBox.IsChecked == true;
            RunTask.shutdownWhenDone = armed;

            if (armed)
                Logger.Log(Program.busy ? "Shutting down once the current task finishes." : "Shutting down once the next task finishes.");
        }

        private void Thumbnail_Click(object sender, PointerPressedEventArgs e)
        {
            ThumbnailView.ThumbnailClick();
        }

        #endregion

        #region Tab switching

        /// <summary> Run only has something to start on the tabs that carry a task. </summary>
        private void UpdateRunButtonState()
        {
            MainTab tab = (MainTab)MainTabs.SelectedIndex;
            RunBtn.IsEnabled = tab is MainTab.Av1an or MainTab.QuickConvert or MainTab.Utilities;
        }

        private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            await ApplySelectedTab();
        }

        /// <summary>
        /// Brings the selected tab's own state up to date. Separate from the handler because the
        /// tab restored from the last session is selected during startup, before the handler is
        /// allowed to do anything, so its setup has to be applied once startup is over.
        /// </summary>
        private async Task ApplySelectedTab()
        {
            MainTab tab = (MainTab)MainTabs.SelectedIndex;
            UpdateRunButtonState();

            if (tab == MainTab.FileList)
                await RefreshFileListUi();

            if (tab == MainTab.TrackList)
                RefreshStreamListUi();

            if (tab == MainTab.QuickConvert)
                QuickConvertUi.ValidatePath();

            if (tab == MainTab.Av1an)
                Av1anUi.ValidatePath();
        }

        #endregion

        #region Links

        private void Discord_Click(object sender, RoutedEventArgs e) => Shell.OpenWithDefaultHandler("https://discord.gg/eJHD2NSJRe");
        private void Patreon_Click(object sender, RoutedEventArgs e) => Shell.OpenWithDefaultHandler("https://patreon.com/n00mkrad");
        private void PayPal_Click(object sender, RoutedEventArgs e) => Shell.OpenWithDefaultHandler("https://www.paypal.com/paypalme/nmkd/10");

        #endregion
    }
}
