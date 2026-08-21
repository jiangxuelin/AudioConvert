using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AudioConvert.Services;
using AudioConvert.ViewModels;

namespace AudioConvert
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private IntPtr _windowHandle;
        private MainWindowViewModel? _viewModel;
        private bool _isClosingAfterTrialPrompt;

        public MainWindow()
        {
            InitializeComponent();

            var trialMembershipStore = new LocalTrialUsageStore();
            _viewModel = new MainWindowViewModel(
                new AudioConverterService(),
                CreateQuotaService(trialMembershipStore),
                CreateTrialMembershipClaimCoordinator(trialMembershipStore));
            DataContext = _viewModel;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
        }

        private IntPtr GetWindowHandle()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                return _windowHandle;
            }

            _windowHandle = new WindowInteropHelper(this).EnsureHandle();
            return _windowHandle;
        }

        private IConversionQuotaService CreateQuotaService(ITrialMembershipStore trialMembershipStore)
        {
#if PACKAGE_TEST_QUOTA
            return new PackageTestConversionQuotaService();
#else
            return new TrialThenStoreConversionQuotaService(
                trialMembershipStore,
                new MicrosoftStoreConversionQuotaService(GetWindowHandle));
#endif
        }

        private TrialMembershipClaimCoordinator CreateTrialMembershipClaimCoordinator(
            ITrialMembershipStore trialMembershipStore)
        {
            return new TrialMembershipClaimCoordinator(
                trialMembershipStore,
                new TrialMembershipDialogPresenter(() => this),
                new MicrosoftStoreReviewService(GetWindowHandle),
                () => DateTimeOffset.UtcNow);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel is null)
            {
                return;
            }

            try
            {
                await _viewModel.RefreshQuotaStatusAsync();
            }
            catch
            {
                // Store status refresh is optional at startup; purchase/execute will report details.
            }
        }

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isClosingAfterTrialPrompt || _viewModel is null)
            {
                return;
            }

            e.Cancel = true;
            _isClosingAfterTrialPrompt = true;
            try
            {
                await _viewModel.TryShowTrialMembershipClaimAsync(TrialMembershipPromptTrigger.ApplicationExit);
            }
            finally
            {
                Close();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
