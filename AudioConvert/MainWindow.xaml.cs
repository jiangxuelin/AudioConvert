using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainWindowViewModel(
                new Services.AudioConverterService(),
                CreateQuotaService());
            DataContext = _viewModel;
            Loaded += MainWindow_Loaded;
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

        private Services.IConversionQuotaService CreateQuotaService()
        {
#if PACKAGE_TEST_QUOTA
            return new Services.PackageTestConversionQuotaService();
#else
            return new Services.MicrosoftStoreConversionQuotaService(GetWindowHandle);
#endif
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
