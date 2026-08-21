using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioConvert
{
    public partial class ResultDialog : Window, INotifyPropertyChanged
    {
        private const string SuccessIconData = "M2,14 L10,22 L24,6";
        private const string FailureIconData = "M6,6 L22,22 M22,6 L6,22";

        private readonly string _outputPath;
        private readonly bool _isFailure;

        private ResultDialog(
            bool isSuccess,
            string title,
            string message,
            string outputPath,
            bool allowRetry)
        {
            InitializeComponent();

            _outputPath = outputPath;
            _isFailure = !isSuccess;
            ResultTitle = title;
            ResultMessage = message;
            IconData = isSuccess ? SuccessIconData : FailureIconData;
            AccentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSuccess ? "#14B8A6" : "#EF4444"));
            IconBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isSuccess ? "#DDFBF5" : "#FEE2E2"));
            PrimaryButtonText = isSuccess ? "打开文件夹" : "重试";
            PrimaryButton.Visibility = isSuccess || allowRetry ? Visibility.Visible : Visibility.Collapsed;

            DataContext = this;
        }

        public string ResultTitle { get; }

        public string ResultMessage { get; }

        public string IconData { get; }

        public Brush AccentBrush { get; }

        public Brush IconBackground { get; }

        public string PrimaryButtonText { get; }

        public bool RetryRequested { get; private set; }

        public static void ShowSuccess(string title, string message, string outputPath)
        {
            var dialog = new ResultDialog(true, title, BuildSuccessMessage(message, outputPath), outputPath, allowRetry: false);
            ShowOwnedDialog(dialog);
        }

        public static bool ShowFailure(string title, string message)
        {
            var dialog = new ResultDialog(false, title, message, string.Empty, allowRetry: true);
            ShowOwnedDialog(dialog);
            return dialog.RetryRequested;
        }

        private static void ShowOwnedDialog(ResultDialog dialog)
        {
            Window? owner = Application.Current?.MainWindow;
            if (owner is not null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }

            using (DialogOwnerDimming.Apply(owner))
            {
                dialog.ShowDialog();
            }
        }

        private static string BuildSuccessMessage(string message, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return message;
            }

            return message + " " + Path.GetFileName(outputPath);
        }

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFailure)
            {
                RetryRequested = true;
                DialogResult = true;
                Close();
                return;
            }

            OpenOutputLocation();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OpenOutputLocation()
        {
            if (string.IsNullOrWhiteSpace(_outputPath))
            {
                return;
            }

            try
            {
                string arguments = File.Exists(_outputPath)
                    ? "/select,\"" + _outputPath + "\""
                    : "\"" + Path.GetDirectoryName(_outputPath) + "\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = arguments,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
