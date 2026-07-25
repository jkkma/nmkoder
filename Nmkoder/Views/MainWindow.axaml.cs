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
            Av1anAdvancedArgsGrid.ItemsSource = Av1anArgRows;
            ThumbnailBox.Source = AppImages.Placeholder;

            ListEntryBase.CheckedChanged += (s, e) => OnStreamCheckedChanged();

            SetUpDragDrop();
            SetUpModifierTracking();

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

            await RefreshFileListUi();

            var packageArg = Program.args.FirstOrDefault(x => x.StartsWith("package="));

            if (packageArg != null)
                await PackageBuild.Run(packageArg.Split('=')[1]);

            if (Nmkoder.Data.Paths.GetExe().Length > 150)
                Logger.Log($"Warning: Nmkoder's installation path is very long ({Nmkoder.Data.Paths.GetExe().Length} characters) - This can lead to problems. It is recommended to move it to a higher directory to reduce the path length.");

            QuickConvertUi.InitAdvFilterGrid();
            Av1anUi.InitAdvFilterGrid();
            UpdateResetSettingsText();
            QuickConvertUi.InitFile();

            _initialized = true;

            if (Program.fileArgs.Length > 0)
                await FileList.HandleFiles(Program.fileArgs, true);
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            SaveUiConfig();
            SaveConfigAv1an();

            // Holding Shift while closing leaves subprocesses (e.g. av1an) running.
            if (!Hotkeys.ShiftHeld)
                ProcessManager.KillAll();

            Program.Cleanup();
        }

        void LoadUiConfig()
        {
            ConfigParser.LoadComboxIndex(FileListModeBox);
            ConfigParser.LoadComboxIndex(TaskModeBox);
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

        public void SaveUiConfig()
        {
            if (!_initialized)
                return;

            ConfigParser.SaveComboxIndex(FileListModeBox);
            ConfigParser.SaveComboxIndex(TaskModeBox);
            // Quick Convert
            ConfigParser.SaveGuiElement(FfmpegContainerBox);
            ConfigParser.SaveComboxIndex(EncVidCodecsBox);
            ConfigParser.SaveComboxIndex(EncAudCodecBox);
            ConfigParser.SaveComboxIndex(EncSubCodecBox);
            ConfigParser.SaveComboxIndex(EncMetaCopySource);
        }

        #endregion

        #region Public control surface used by the task layer

        public TextBox FfmpegOutputBox => OutputPathBox;
        public TextBox CustomArgsInBox => EncCustomArgsIn;
        public TextBox CustomArgsOutBox => EncCustomArgsOut;
        public ComboBox Av1anColorsBox => Av1anColorSpaceBox;

        /// <summary> Index of the selected top-level tab (0 = File List, 1 = Track List, ...). </summary>
        public int SelectedMainTab
        {
            get => MainTabs.SelectedIndex;
            set => Dispatcher.UIThread.Post(() => MainTabs.SelectedIndex = value);
        }

        public bool IsInFocus() => IsActive;

        public RunTask.TaskType SelectedTask
        {
            get
            {
                switch (MainTabs.SelectedIndex)
                {
                    case 2: return RunTask.TaskType.Convert;
                    case 3: return RunTask.TaskType.Av1an;
                    case 4: return GetUtilsTaskType();
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

        public void SetStatus(string str)
        {
            Logger.Log(str, true);
            RunOnUi(() => CurrentActionLabel.Text = str);
        }

        public void SetWorking(bool state, bool allowCancel = true)
        {
            Logger.Log($"SetWorking({state})", true);
            SetProgress(0, false);

            RunOnUi(() =>
            {
                RunBtn.IsEnabled = !state;
                RunBtn.IsVisible = !state;
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
            RunTask.Cancel("Canceled manually.", true);
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            SuspendResume.SuspendProcs(!SuspendResume.frozen);
        }

        private void Thumbnail_Click(object sender, PointerPressedEventArgs e)
        {
            ThumbnailView.ThumbnailClick();
        }

        #endregion

        #region Tab switching

        private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized)
                return;

            int index = MainTabs.SelectedIndex;
            RunBtn.IsEnabled = index == 2 || index == 3 || index == 4;

            if (index == 0)
                await RefreshFileListUi();

            if (index == 1)
                RefreshStreamListUi();

            if (index == 2)
                QuickConvertUi.ValidatePath();

            if (index == 3)
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
