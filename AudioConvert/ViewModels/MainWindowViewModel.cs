using AudioConvert;
using AudioConvert.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AudioConvert.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private const string AudioFileFilter =
            "音频文件 (*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac;*.mgg;*.mflac;*.ncm;*.kgg;*.kgma;*.kwm)|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac;*.mgg;*.mflac;*.ncm;*.kgg;*.kgma;*.kwm";
        private const string LoginButtonDefaultText = "登录";
        private const string LoginButtonFallbackInitials = "MS";

        private readonly IAudioConverterService _converterService;
        private readonly IConversionQuotaService _quotaService;
        private readonly AudioProcessingWorkflow _processingWorkflow;
        private readonly TrialMembershipClaimCoordinator? _trialMembershipClaimCoordinator;

        public MainWindowViewModel()
            : this(new AudioConverterService(), CreateDefaultQuotaService(), null)
        {
        }

        public MainWindowViewModel(IAudioConverterService converterService)
            : this(converterService, CreateDefaultQuotaService(), null)
        {
        }

        public MainWindowViewModel(
            IAudioConverterService converterService,
            IConversionQuotaService quotaService)
            : this(converterService, quotaService, null)
        {
        }

        public MainWindowViewModel(
            IAudioConverterService converterService,
            IConversionQuotaService quotaService,
            TrialMembershipClaimCoordinator? trialMembershipClaimCoordinator)
        {
            _converterService = converterService ?? throw new ArgumentNullException(nameof(converterService));
            _quotaService = quotaService ?? throw new ArgumentNullException(nameof(quotaService));
            _processingWorkflow = new AudioProcessingWorkflow(_quotaService);
            _trialMembershipClaimCoordinator = trialMembershipClaimCoordinator;

            Tools = new ObservableCollection<ToolOption>
            {
                new ToolOption(
                    "Convert",
                    "音频格式转换",
                    "支持 MP3 / FLAC / WAV / OGG / NCM",
                    "M7,7 L17,7 M17,7 L14,4 M17,7 L14,10 M17,17 L7,17 M7,17 L10,14 M7,17 L10,20 M5,12 A7,7 0 0,1 12,5 M19,12 A7,7 0 0,1 12,19",
                    true),
                new ToolOption(
                    "Trim",
                    "音频切割",
                    "按起止时间裁剪片段",
                    "M5,5 L19,19 M19,5 L5,19 M4,18 A2,2 0 1,0 8,18 A2,2 0 1,0 4,18 M16,18 A2,2 0 1,0 20,18 A2,2 0 1,0 16,18 M9,12 L15,12"),
                new ToolOption(
                    "Merge",
                    "音频合并",
                    "按列表顺序合并多个文件",
                    "M4,7 L10,7 C13,7 13,12 16,12 L21,12 M4,17 L10,17 C13,17 13,12 16,12 M18,9 L21,12 L18,15"),
                new ToolOption(
                    "Compress",
                    "音频压缩",
                    "三档质量压缩为 MP3",
                    "M5,7 L5,17 M10,4 L10,20 M15,8 L15,16 M20,10 L20,14 M7,12 L17,12 M14,9 L17,12 L14,15")
            };

            OutputFormats = new ObservableCollection<OutputFormatOption>
            {
                new OutputFormatOption(AudioOutputFormat.Mp3),
                new OutputFormatOption(AudioOutputFormat.Wav),
                new OutputFormatOption(AudioOutputFormat.Flac),
                new OutputFormatOption(AudioOutputFormat.Ogg)
            };

            CompressionQualities = new ObservableCollection<CompressionQualityOption>
            {
                new CompressionQualityOption("高质量 192k", "192k"),
                new CompressionQualityOption("中等质量 128k", "128k"),
                new CompressionQualityOption("低体积 96k", "96k")
            };

            MergeFiles = new ObservableCollection<string>();
            MergeFiles.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(MergeFileCountText));
                RaiseCommandStates();
            };

            _selectedOutputFormat = OutputFormats[0];
            _selectedMergeOutputFormat = OutputFormats[0];
            _selectedCompressionQuality = CompressionQualities[1];

            SelectToolCommand = new RelayCommand(parameter =>
            {
                SelectTool(parameter?.ToString() ?? "Convert");
                return Task.CompletedTask;
            }, _ => !IsBusy);

            SelectSingleFileCommand = new RelayCommand(_ =>
            {
                SelectSingleFile();
                return Task.CompletedTask;
            }, _ => !IsBusy && !IsMergeTool);

            SelectMergeFilesCommand = new RelayCommand(_ =>
            {
                SelectMergeFiles();
                return Task.CompletedTask;
            }, _ => !IsBusy && IsMergeTool);

            RemoveMergeFileCommand = new RelayCommand(_ =>
            {
                RemoveSelectedMergeFile();
                return Task.CompletedTask;
            }, _ => !IsBusy && !string.IsNullOrWhiteSpace(SelectedMergeFile));

            MoveMergeFileUpCommand = new RelayCommand(_ =>
            {
                MoveSelectedMergeFile(-1);
                return Task.CompletedTask;
            }, _ => CanMoveSelectedMergeFile(-1));

            MoveMergeFileDownCommand = new RelayCommand(_ =>
            {
                MoveSelectedMergeFile(1);
                return Task.CompletedTask;
            }, _ => CanMoveSelectedMergeFile(1));

            ExecuteCommand = new RelayCommand(async _ => await ExecuteCurrentOperationAsync(), _ => CanExecuteCurrentOperation());
            RetryCommand = new RelayCommand(async _ => await ExecuteCurrentOperationAsync(), _ => CanExecuteCurrentOperation());
            LoginCommand = new RelayCommand(async _ => await LoginAsync(), _ => !IsBusy);
            PurchaseQuotaCommand = new RelayCommand(async _ => await PurchaseQuotaAsync(), _ => !IsBusy);
            SelectFileCommand = SelectSingleFileCommand;

            StatusText = "准备就绪";
            QuotaStatusText = "剩余次数：正在读取...";
            TrialStatusText = "试用会员：正在读取...";
            StoreAccountDisplayName = "Microsoft Store";
            StoreAccountInitials = LoginButtonFallbackInitials;
        }

        private static IConversionQuotaService CreateDefaultQuotaService()
        {
#if PACKAGE_TEST_QUOTA
            return new PackageTestConversionQuotaService();
#else
            return new TrialThenStoreConversionQuotaService(
                new LocalTrialUsageStore(),
                new MicrosoftStoreConversionQuotaService());
#endif
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value)
                {
                    return;
                }

                _isBusy = value;
                OnPropertyChanged();
                RaiseCommandStates();
            }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged();
            }
        }

        private string _selectedToolKey = "Convert";
        public string SelectedToolKey
        {
            get => _selectedToolKey;
            private set
            {
                if (_selectedToolKey == value)
                {
                    return;
                }

                _selectedToolKey = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConvertTool));
                OnPropertyChanged(nameof(IsTrimTool));
                OnPropertyChanged(nameof(IsMergeTool));
                OnPropertyChanged(nameof(IsCompressTool));
                OnPropertyChanged(nameof(CurrentToolTitle));
                OnPropertyChanged(nameof(CurrentToolDescription));
                RaiseCommandStates();
            }
        }

        private string _selectedFilePath = string.Empty;
        public string SelectedFilePath
        {
            get => _selectedFilePath;
            private set
            {
                if (_selectedFilePath == value)
                {
                    return;
                }

                _selectedFilePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFileDisplayText));
                RaiseCommandStates();
            }
        }

        private string? _selectedMergeFile;
        public string? SelectedMergeFile
        {
            get => _selectedMergeFile;
            set
            {
                if (_selectedMergeFile == value)
                {
                    return;
                }

                _selectedMergeFile = value;
                OnPropertyChanged();
                RaiseCommandStates();
            }
        }

        private OutputFormatOption _selectedOutputFormat;
        public OutputFormatOption SelectedOutputFormat
        {
            get => _selectedOutputFormat;
            set
            {
                if (_selectedOutputFormat == value)
                {
                    return;
                }

                _selectedOutputFormat = value;
                OnPropertyChanged();
            }
        }

        private OutputFormatOption _selectedMergeOutputFormat;
        public OutputFormatOption SelectedMergeOutputFormat
        {
            get => _selectedMergeOutputFormat;
            set
            {
                if (_selectedMergeOutputFormat == value)
                {
                    return;
                }

                _selectedMergeOutputFormat = value;
                OnPropertyChanged();
            }
        }

        private CompressionQualityOption _selectedCompressionQuality;
        public CompressionQualityOption SelectedCompressionQuality
        {
            get => _selectedCompressionQuality;
            set
            {
                if (_selectedCompressionQuality == value)
                {
                    return;
                }

                _selectedCompressionQuality = value;
                OnPropertyChanged();
            }
        }

        private int _trimStartHours;
        public int TrimStartHours
        {
            get => _trimStartHours;
            set => SetTrimValue(ref _trimStartHours, value);
        }

        private int _trimStartMinutes;
        public int TrimStartMinutes
        {
            get => _trimStartMinutes;
            set => SetTrimValue(ref _trimStartMinutes, value);
        }

        private int _trimStartSeconds;
        public int TrimStartSeconds
        {
            get => _trimStartSeconds;
            set => SetTrimValue(ref _trimStartSeconds, value);
        }

        private int _trimEndHours;
        public int TrimEndHours
        {
            get => _trimEndHours;
            set => SetTrimValue(ref _trimEndHours, value);
        }

        private int _trimEndMinutes;
        public int TrimEndMinutes
        {
            get => _trimEndMinutes;
            set => SetTrimValue(ref _trimEndMinutes, value);
        }

        private int _trimEndSeconds = 30;
        public int TrimEndSeconds
        {
            get => _trimEndSeconds;
            set => SetTrimValue(ref _trimEndSeconds, value);
        }

        public ObservableCollection<ToolOption> Tools { get; }

        public ObservableCollection<OutputFormatOption> OutputFormats { get; }

        public ObservableCollection<CompressionQualityOption> CompressionQualities { get; }

        public ObservableCollection<string> MergeFiles { get; }

        public bool IsConvertTool => SelectedToolKey == "Convert";

        public bool IsTrimTool => SelectedToolKey == "Trim";

        public bool IsMergeTool => SelectedToolKey == "Merge";

        public bool IsCompressTool => SelectedToolKey == "Compress";

        public string CurrentToolTitle => Tools.First(tool => tool.Key == SelectedToolKey).Title;

        public string CurrentToolDescription => Tools.First(tool => tool.Key == SelectedToolKey).Description;

        public string SelectedFileDisplayText =>
            string.IsNullOrWhiteSpace(SelectedFilePath)
                ? "尚未选择音频文件"
                : Path.GetFileName(SelectedFilePath);

        public string MergeFileCountText => MergeFiles.Count == 0
            ? "尚未选择合并文件"
            : $"已选择 {MergeFiles.Count} 个文件";

        private string _quotaStatusText = string.Empty;
        public string QuotaStatusText
        {
            get => _quotaStatusText;
            private set
            {
                if (_quotaStatusText == value)
                {
                    return;
                }

                _quotaStatusText = value;
                OnPropertyChanged();
            }
        }

        private string _trialStatusText = string.Empty;
        public string TrialStatusText
        {
            get => _trialStatusText;
            private set
            {
                if (_trialStatusText == value)
                {
                    return;
                }

                _trialStatusText = value;
                OnPropertyChanged();
            }
        }

        private bool _isStoreAccountSynced;
        public bool IsStoreAccountSynced
        {
            get => _isStoreAccountSynced;
            private set
            {
                if (_isStoreAccountSynced == value)
                {
                    return;
                }

                _isStoreAccountSynced = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoginButtonContent));
                OnPropertyChanged(nameof(LoginButtonToolTip));
            }
        }

        private string _storeAccountDisplayName = string.Empty;
        public string StoreAccountDisplayName
        {
            get => _storeAccountDisplayName;
            private set
            {
                if (_storeAccountDisplayName == value)
                {
                    return;
                }

                _storeAccountDisplayName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoginButtonToolTip));
            }
        }

        private string _storeAccountInitials = LoginButtonFallbackInitials;
        public string StoreAccountInitials
        {
            get => _storeAccountInitials;
            private set
            {
                if (_storeAccountInitials == value)
                {
                    return;
                }

                _storeAccountInitials = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoginButtonContent));
            }
        }

        public string LoginButtonContent => IsStoreAccountSynced ? StoreAccountInitials : LoginButtonDefaultText;

        public string LoginButtonToolTip => IsStoreAccountSynced
            ? "Microsoft Store 账号已同步：" + StoreAccountDisplayName
            : "登录 Microsoft Store";

        public ICommand SelectToolCommand { get; }

        public ICommand SelectSingleFileCommand { get; }

        public ICommand SelectMergeFilesCommand { get; }

        public ICommand RemoveMergeFileCommand { get; }

        public ICommand MoveMergeFileUpCommand { get; }

        public ICommand MoveMergeFileDownCommand { get; }

        public ICommand ExecuteCommand { get; }

        public ICommand RetryCommand { get; }

        public ICommand LoginCommand { get; }

        public ICommand PurchaseQuotaCommand { get; }

        public ICommand SelectFileCommand { get; }

        private void SelectTool(string toolKey)
        {
            if (Tools.All(tool => tool.Key != toolKey))
            {
                toolKey = "Convert";
            }

            SelectedToolKey = toolKey;
            foreach (ToolOption tool in Tools)
            {
                tool.IsSelected = tool.Key == toolKey;
            }

            StatusText = "准备就绪";
        }

        private void SelectSingleFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择音频文件",
                Filter = AudioFileFilter,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SelectedFilePath = dialog.FileName;
            StatusText = "已选择：" + Path.GetFileName(dialog.FileName);
        }

        private void SelectMergeFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择需要合并的音频文件",
                Filter = AudioFileFilter,
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            foreach (string fileName in dialog.FileNames)
            {
                if (!MergeFiles.Contains(fileName))
                {
                    MergeFiles.Add(fileName);
                }
            }

            if (MergeFiles.Count > 0)
            {
                SelectedMergeFile = MergeFiles[MergeFiles.Count - 1];
            }

            StatusText = MergeFileCountText;
        }

        private void RemoveSelectedMergeFile()
        {
            string? selectedFile = SelectedMergeFile;
            if (string.IsNullOrWhiteSpace(selectedFile))
            {
                return;
            }

            int index = MergeFiles.IndexOf(selectedFile!);
            if (index < 0)
            {
                return;
            }

            MergeFiles.RemoveAt(index);
            if (MergeFiles.Count == 0)
            {
                SelectedMergeFile = null;
            }
            else
            {
                SelectedMergeFile = MergeFiles[Math.Min(index, MergeFiles.Count - 1)];
            }

            StatusText = MergeFileCountText;
        }

        private void MoveSelectedMergeFile(int direction)
        {
            string? selectedFile = SelectedMergeFile;
            if (!CanMoveSelectedMergeFile(direction) || string.IsNullOrWhiteSpace(selectedFile))
            {
                return;
            }

            int oldIndex = MergeFiles.IndexOf(selectedFile!);
            int newIndex = oldIndex + direction;
            MergeFiles.Move(oldIndex, newIndex);
            SelectedMergeFile = MergeFiles[newIndex];
        }

        private bool CanMoveSelectedMergeFile(int direction)
        {
            string? selectedFile = SelectedMergeFile;
            if (IsBusy || string.IsNullOrWhiteSpace(selectedFile))
            {
                return false;
            }

            int index = MergeFiles.IndexOf(selectedFile!);
            if (index < 0)
            {
                return false;
            }

            int newIndex = index + direction;
            return newIndex >= 0 && newIndex < MergeFiles.Count;
        }

        private bool CanExecuteCurrentOperation()
        {
            if (IsBusy)
            {
                return false;
            }

            if (IsMergeTool)
            {
                return MergeFiles.Count >= 2;
            }

            return !string.IsNullOrWhiteSpace(SelectedFilePath);
        }

        private async Task ExecuteCurrentOperationAsync()
        {
            if (!CanExecuteCurrentOperation())
            {
                StatusText = IsMergeTool ? "请至少选择两个音频文件。" : "请先选择音频文件。";
                return;
            }

            string successTitle = IsConvertTool ? "转换成功" : "处理成功";

            bool retry;
            do
            {
                retry = false;
                Func<Task<ConvertResult>> operation;
                try
                {
                    operation = BuildCurrentOperation();
                }
                catch (Exception exception)
                {
                    StatusText = "处理失败";
                    ResultDialog.ShowFailure("处理失败", exception.Message);
                    return;
                }

                IsBusy = true;
                AudioProcessingWorkflowResult workflowResult;
                try
                {
                    workflowResult = await _processingWorkflow.ExecuteAsync(
                        operation,
                        successTitle,
                        new Progress<string>(message => StatusText = message));
                }
                finally
                {
                    IsBusy = false;
                }

                if (workflowResult.QuotaResult is not null)
                {
                    UpdateQuotaStatus(workflowResult.QuotaResult);
                    StatusText = workflowResult.QuotaResult.IsUserCanceled ? "已取消购买" : "次数不足";
                    if (!workflowResult.QuotaResult.IsUserCanceled)
                    {
                        ResultDialog.ShowFailure("需要购买次数包", workflowResult.QuotaResult.Message);
                    }

                    return;
                }

                if (workflowResult.IsSuccess)
                {
                    ConversionQuotaResult? consumeResult = workflowResult.ConsumeResult;
                    if (consumeResult is not null)
                    {
                        UpdateQuotaStatus(consumeResult);
                        await RefreshTrialStatusAsync();
                    }

                    StatusText = consumeResult?.IsSuccess != false ? "处理完成" : "处理完成，扣次待同步";
                    string successMessage = consumeResult?.IsSuccess != false
                        ? "音频处理已完成。"
                        : "音频处理已完成，但次数扣减尚未同步。请保持联网后再次处理。";
                    ResultDialog.ShowSuccess(
                        workflowResult.SuccessTitle,
                        successMessage,
                        workflowResult.ConvertResult?.OutputPath ?? string.Empty);
                }
                else
                {
                    StatusText = "处理失败";
                    retry = ResultDialog.ShowFailure(
                        "处理失败",
                        workflowResult.ConvertResult?.ErrorMessage ?? "未知错误");
                }
            }
            while (retry);
        }

        private async Task PurchaseQuotaAsync()
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            StatusText = "正在读取额度...";
            ConversionQuotaPurchaseInfo purchaseInfo;
            try
            {
                purchaseInfo = await _quotaService.GetPurchaseInfoAsync();
                UpdateQuotaStatus(purchaseInfo);
            }
            finally
            {
                IsBusy = false;
            }

            bool buyRequested = PurchaseQuotaDialog.ShowPurchasePrompt(purchaseInfo);
            if (!buyRequested)
            {
                StatusText = purchaseInfo.IsSuccess ? "已取消购买" : purchaseInfo.Message;
                await TryShowTrialMembershipClaimAsync(TrialMembershipPromptTrigger.PurchaseClosed);
                return;
            }

            IsBusy = true;
            StatusText = "正在打开购买窗口...";
            try
            {
                ConversionQuotaResult result = await _quotaService.PurchaseQuotaAsync();
                UpdateQuotaStatus(result);
                await RefreshTrialStatusAsync();
                await UpdateStoreAccountStateAsync(result.IsSuccess);
                StatusText = result.IsSuccess ? "购买完成" : result.Message;
                if (!result.IsSuccess && !result.IsUserCanceled)
                {
                    ResultDialog.ShowFailure("购买失败", result.Message);
                }

                if (!result.IsSuccess)
                {
                    await TryShowTrialMembershipClaimAsync(TrialMembershipPromptTrigger.PurchaseClosed);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoginAsync()
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            StatusText = "正在连接 Microsoft Store...";
            try
            {
                ConversionQuotaResult result = await _quotaService.SignInAsync();
                UpdateQuotaStatus(result);
                await RefreshTrialStatusAsync();
                await UpdateStoreAccountStateAsync(result.IsSuccess);
                StatusText = result.IsSuccess ? "Microsoft Store 账号已同步" : result.Message;
                if (!result.IsSuccess && !result.IsUserCanceled)
                {
                    ResultDialog.ShowFailure("登录失败", result.Message);
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task RefreshQuotaStatusAsync()
        {
            ConversionQuotaResult result = await _quotaService.RefreshBalanceAsync();
            UpdateQuotaStatus(result);
            bool isTrialAvailable = await RefreshTrialStatusAsync();
            await UpdateStoreAccountStateAsync(result.IsSuccess && !isTrialAvailable);
        }

        private async Task<bool> RefreshTrialStatusAsync()
        {
            if (_quotaService is TrialThenStoreConversionQuotaService trialQuotaService)
            {
                TrialQuotaStatus status = await trialQuotaService.GetTrialStatusAsync();
                TrialStatusText = status.DisplayText;
                return status.IsAvailable;
            }

            TrialStatusText = "试用会员：不可用";
            return false;
        }

        public async Task<bool> TryShowTrialMembershipClaimAsync(TrialMembershipPromptTrigger trigger)
        {
            if (_trialMembershipClaimCoordinator is null)
            {
                return false;
            }

            bool claimed = await _trialMembershipClaimCoordinator.TryShowClaimPromptAsync(trigger);
            if (claimed)
            {
                await RefreshTrialStatusAsync();
            }

            return claimed;
        }

        private async Task UpdateStoreAccountStateAsync(bool isSynced)
        {
            if (!isSynced)
            {
                IsStoreAccountSynced = false;
                return;
            }

            string displayName = await TryGetWindowsAccountDisplayNameAsync();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "Microsoft Store";
            }

            StoreAccountDisplayName = displayName;
            StoreAccountInitials = BuildAccountInitials(displayName);
            IsStoreAccountSynced = true;
        }

        private static async Task<string> TryGetWindowsAccountDisplayNameAsync()
        {
            try
            {
                IReadOnlyList<Windows.System.User> users = await Windows.System.User.FindAllAsync();
                Windows.System.User? user = users.FirstOrDefault();
                if (user is null)
                {
                    return string.Empty;
                }

                object displayName = await user.GetPropertyAsync(Windows.System.KnownUserProperties.DisplayName);
                if (displayName is string displayNameText && !string.IsNullOrWhiteSpace(displayNameText))
                {
                    return displayNameText.Trim();
                }

                object accountName = await user.GetPropertyAsync(Windows.System.KnownUserProperties.AccountName);
                if (accountName is string accountNameText && !string.IsNullOrWhiteSpace(accountNameText))
                {
                    return accountNameText.Trim();
                }
            }
            catch
            {
                // Store sync is still valid even when Windows privacy settings hide account info.
            }

            return string.Empty;
        }

        private static string BuildAccountInitials(string displayName)
        {
            string normalizedName = displayName.Trim();
            int emailSeparatorIndex = normalizedName.IndexOf('@');
            if (emailSeparatorIndex > 0)
            {
                normalizedName = normalizedName.Substring(0, emailSeparatorIndex);
            }

            char[] separators = { ' ', '.', '_', '-' };
            string[] nameParts = normalizedName
                .Split(separators, StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Any(char.IsLetterOrDigit))
                .ToArray();

            IEnumerable<char> initials = nameParts.Length >= 2
                ? nameParts.Take(2).Select(part => part.First(char.IsLetterOrDigit))
                : normalizedName.Where(char.IsLetterOrDigit).Take(2);

            string result = new string(initials.ToArray()).ToUpperInvariant();
            return string.IsNullOrWhiteSpace(result) ? LoginButtonFallbackInitials : result;
        }

        private void UpdateQuotaStatus(ConversionQuotaResult result)
        {
            if (result.BalanceRemaining.HasValue)
            {
                QuotaStatusText = "剩余次数：" + result.BalanceRemaining.Value;
                return;
            }

            QuotaStatusText = result.IsSuccess ? "剩余次数：已同步" : "剩余次数：读取失败";
        }

        private void UpdateQuotaStatus(ConversionQuotaPurchaseInfo result)
        {
            if (result.BalanceRemaining.HasValue)
            {
                QuotaStatusText = "剩余次数：" + result.BalanceRemaining.Value;
                return;
            }

            QuotaStatusText = result.IsSuccess ? "剩余次数：已同步" : "剩余次数：读取失败";
        }

        private Func<Task<ConvertResult>> BuildCurrentOperation()
        {
            if (IsTrimTool)
            {
                TimeSpan startTime = BuildTrimTime(TrimStartHours, TrimStartMinutes, TrimStartSeconds);
                TimeSpan endTime = BuildTrimTime(TrimEndHours, TrimEndMinutes, TrimEndSeconds);
                return () => _converterService.TrimAsync(
                    SelectedFilePath,
                    startTime,
                    endTime,
                    new Progress<string>(message => StatusText = message));
            }

            if (IsMergeTool)
            {
                string[] files = MergeFiles.ToArray();
                AudioOutputFormat format = SelectedMergeOutputFormat.Format;
                return () => _converterService.MergeAsync(
                    files,
                    format,
                    new Progress<string>(message => StatusText = message));
            }

            if (IsCompressTool)
            {
                string bitrate = SelectedCompressionQuality.Bitrate;
                return () => _converterService.CompressAsync(
                    SelectedFilePath,
                    bitrate,
                    new Progress<string>(message => StatusText = message));
            }

            AudioOutputFormat outputFormat = SelectedOutputFormat.Format;
            return () => _converterService.ConvertAsync(
                SelectedFilePath,
                outputFormat,
                new Progress<string>(message => StatusText = message));
        }

        private static TimeSpan BuildTrimTime(int hours, int minutes, int seconds)
        {
            if (hours < 0 || minutes < 0 || seconds < 0)
            {
                throw new FormatException("时间不能为负数。");
            }

            if (minutes >= 60 || seconds >= 60)
            {
                throw new FormatException("分钟和秒数必须小于 60。");
            }

            return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        private void SetTrimValue(ref int storage, int value, [CallerMemberName] string? propertyName = null)
        {
            if (storage == value)
            {
                return;
            }

            storage = value;
            OnPropertyChanged(propertyName);
        }

        private void RaiseCommandStates()
        {
            ((RelayCommand)SelectToolCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SelectSingleFileCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SelectMergeFilesCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RemoveMergeFileCommand).RaiseCanExecuteChanged();
            ((RelayCommand)MoveMergeFileUpCommand).RaiseCanExecuteChanged();
            ((RelayCommand)MoveMergeFileDownCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ExecuteCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RetryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PurchaseQuotaCommand).RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ToolOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ToolOption(string key, string title, string description, string iconData, bool isSelected = false)
        {
            Key = key;
            Title = title;
            Description = description;
            IconData = iconData;
            _isSelected = isSelected;
        }

        public string Key { get; }

        public string Title { get; }

        public string Description { get; }

        public string IconData { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class OutputFormatOption
    {
        public OutputFormatOption(AudioOutputFormat format)
        {
            Format = format;
            DisplayName = format.GetDisplayName();
        }

        public AudioOutputFormat Format { get; }

        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }

    public sealed class CompressionQualityOption
    {
        public CompressionQualityOption(string displayName, string bitrate)
        {
            DisplayName = displayName;
            Bitrate = bitrate;
        }

        public string DisplayName { get; }

        public string Bitrate { get; }

        public override string ToString() => DisplayName;
    }

    public class RelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
        {
            try
            {
                await _execute(parameter);
            }
            catch (Exception exception)
            {
                ResultDialog.ShowFailure("操作失败", exception.Message);
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
