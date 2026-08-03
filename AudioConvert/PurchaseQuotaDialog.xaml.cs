using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AudioConvert.Services;

namespace AudioConvert
{
    public partial class PurchaseQuotaDialog : Window, INotifyPropertyChanged
    {
        private PurchaseQuotaDialog(ConversionQuotaPurchaseInfo purchaseInfo)
        {
            InitializeComponent();

            BalanceText = purchaseInfo.BalanceRemaining.HasValue
                ? "当前剩余额度：" + purchaseInfo.BalanceRemaining.Value + " 次"
                : "当前剩余额度：读取失败";

            ConversionQuotaPurchaseOption? option = purchaseInfo.PurchaseOption;
            PackageTitle = option?.Title ?? "套餐读取失败";
            PackageDescription = option?.Description ?? purchaseInfo.Message;
            PackagePrice = option?.FormattedPrice ?? "--";
            PackageMeta = option is null
                ? purchaseInfo.Message
                : option.QuantityText + " / " + option.ProductKind;
            CanBuy = purchaseInfo.IsSuccess && option is not null;

            DataContext = this;
        }

        public string BalanceText { get; }

        public string PackageTitle { get; }

        public string PackageDescription { get; }

        public string PackagePrice { get; }

        public string PackageMeta { get; }

        public bool CanBuy { get; }

        public bool BuyRequested { get; private set; }

        public static bool ShowPurchasePrompt(ConversionQuotaPurchaseInfo purchaseInfo)
        {
            var dialog = new PurchaseQuotaDialog(purchaseInfo);
            Window? owner = Application.Current?.MainWindow;
            double? previousOpacity = null;
            if (owner is not null && owner.IsVisible)
            {
                dialog.Owner = owner;
                previousOpacity = owner.Opacity;
                owner.Opacity = 0.9;
            }

            try
            {
                dialog.ShowDialog();
                return dialog.BuyRequested;
            }
            finally
            {
                if (owner is not null && previousOpacity.HasValue)
                {
                    owner.Opacity = previousOpacity.Value;
                }
            }
        }

        private void BuyButton_Click(object sender, RoutedEventArgs e)
        {
            BuyRequested = true;
            DialogResult = true;
            Close();
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
