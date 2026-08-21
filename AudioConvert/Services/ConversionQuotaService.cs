using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace AudioConvert.Services
{
    public interface IConversionQuotaService
    {
        Task<ConversionQuotaResult> SignInAsync();

        Task<ConversionQuotaResult> RefreshBalanceAsync();

        Task<ConversionQuotaResult> EnsureQuotaAsync();

        Task<ConversionQuotaResult> PurchaseQuotaAsync();

        Task<ConversionQuotaResult> ConsumeOneAsync();

        Task<ConversionQuotaPurchaseInfo> GetPurchaseInfoAsync();
    }

    public sealed class ConversionQuotaPurchaseInfo
    {
        private ConversionQuotaPurchaseInfo(
            bool isSuccess,
            uint? balanceRemaining,
            ConversionQuotaPurchaseOption? purchaseOption,
            string message)
        {
            IsSuccess = isSuccess;
            BalanceRemaining = balanceRemaining;
            PurchaseOption = purchaseOption;
            Message = message;
        }

        public bool IsSuccess { get; }

        public uint? BalanceRemaining { get; }

        public ConversionQuotaPurchaseOption? PurchaseOption { get; }

        public string Message { get; }

        public static ConversionQuotaPurchaseInfo Success(
            uint? balanceRemaining,
            ConversionQuotaPurchaseOption purchaseOption,
            string message) =>
            new ConversionQuotaPurchaseInfo(true, balanceRemaining, purchaseOption, message);

        public static ConversionQuotaPurchaseInfo Failure(
            string message,
            uint? balanceRemaining = null,
            ConversionQuotaPurchaseOption? purchaseOption = null) =>
            new ConversionQuotaPurchaseInfo(false, balanceRemaining, purchaseOption, message);
    }

    public sealed class ConversionQuotaPurchaseOption
    {
        public ConversionQuotaPurchaseOption(
            string storeId,
            string title,
            string description,
            string formattedPrice,
            string quantityText,
            string productKind)
        {
            StoreId = storeId;
            Title = title;
            Description = description;
            FormattedPrice = formattedPrice;
            QuantityText = quantityText;
            ProductKind = productKind;
        }

        public string StoreId { get; }

        public string Title { get; }

        public string Description { get; }

        public string FormattedPrice { get; }

        public string QuantityText { get; }

        public string ProductKind { get; }
    }

    public sealed class ConversionQuotaResult
    {
        private ConversionQuotaResult(
            bool isSuccess,
            bool isUserCanceled,
            uint? balanceRemaining,
            string message)
        {
            IsSuccess = isSuccess;
            IsUserCanceled = isUserCanceled;
            BalanceRemaining = balanceRemaining;
            Message = message;
        }

        public bool IsSuccess { get; }

        public bool IsUserCanceled { get; }

        public uint? BalanceRemaining { get; }

        public string Message { get; }

        public static ConversionQuotaResult Success(uint? balanceRemaining, string message) =>
            new ConversionQuotaResult(true, false, balanceRemaining, message);

        public static ConversionQuotaResult Failure(string message, uint? balanceRemaining = null) =>
            new ConversionQuotaResult(false, false, balanceRemaining, message);

        public static ConversionQuotaResult Canceled(string message, uint? balanceRemaining = null) =>
            new ConversionQuotaResult(false, true, balanceRemaining, message);
    }

    public sealed class MicrosoftStoreConversionQuotaService : IConversionQuotaService
    {
        private const string ProductionProductStoreId = "9N9MRSLLPJ1G";
        // Replace this after the Partner Center test consumable SKU is created.
        private const string IntegrationTestProductStoreId = ProductionProductStoreId;
#if MICROSOFT_STORE_INTEGRATION_TEST
        private const string ProductStoreId = IntegrationTestProductStoreId;
#else
        private const string ProductStoreId = ProductionProductStoreId;
#endif
        private const string ExpectedPackageIdentityName = "Estherrrr.477589C055491";
        private const string StoreManagedConsumableKind = "Consumable";
        private const string UnmanagedConsumableKind = "UnmanagedConsumable";
        private const string DurableKind = "Durable";
        private const string MicrosoftStoreSettingsUri = "ms-windows-store://settings";
        private const uint ConsumptionQuantity = 1;

        private readonly Func<IntPtr> _windowHandleProvider;
        private readonly string _pendingConsumptionFilePath;
        private StoreContext? _storeContext;
        private bool _isInitializedForWindow;

        public MicrosoftStoreConversionQuotaService()
            : this(() => IntPtr.Zero)
        {
        }

        public MicrosoftStoreConversionQuotaService(Func<IntPtr> windowHandleProvider)
        {
            _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
            _pendingConsumptionFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioConvert",
                "pending-consumptions.txt");
        }

        public async Task<ConversionQuotaResult> SignInAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return readinessFailure;
            }

            bool isStoreOpened = await Windows.System.Launcher.LaunchUriAsync(new Uri(MicrosoftStoreSettingsUri));
            if (!isStoreOpened)
            {
                return ConversionQuotaResult.Failure("无法打开 Microsoft Store 登录页面。请手动打开 Microsoft Store，在右上角账号入口登录后重试。");
            }

            ConversionQuotaResult pendingResult = await FlushPendingConsumptionsAsync();
            if (!pendingResult.IsSuccess)
            {
                return pendingResult;
            }

            ConversionQuotaResult balanceResult = await GetBalanceAsync("Microsoft Store 账号状态已同步。");
            if (balanceResult.IsSuccess)
            {
                return balanceResult;
            }

            return ConversionQuotaResult.Failure(
                "已打开 Microsoft Store 登录页面。请在 Microsoft Store 中登录账号后，返回本应用再次点击登录同步。"
                + Environment.NewLine
                + balanceResult.Message,
                balanceResult.BalanceRemaining);
        }

        public async Task<ConversionQuotaResult> RefreshBalanceAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return readinessFailure;
            }

            ConversionQuotaResult pendingResult = await FlushPendingConsumptionsAsync();
            if (!pendingResult.IsSuccess)
            {
                return pendingResult;
            }

            return await GetBalanceAsync();
        }

        public async Task<ConversionQuotaResult> EnsureQuotaAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return readinessFailure;
            }

            ConversionQuotaResult pendingResult = await FlushPendingConsumptionsAsync();
            if (!pendingResult.IsSuccess)
            {
                return pendingResult;
            }

            ConversionQuotaResult balanceResult = await GetBalanceAsync();
            if (!balanceResult.IsSuccess)
            {
                return balanceResult;
            }

            if ((balanceResult.BalanceRemaining ?? 0) > 0)
            {
                return balanceResult;
            }

            return await PurchaseQuotaAsync();
        }

        public async Task<ConversionQuotaResult> PurchaseQuotaAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return readinessFailure;
            }

            try
            {
                StoreContext context = GetStoreContext();
                StoreProduct product = await GetQuotaProductAsync(context);
                if (!string.Equals(product.ProductKind, StoreManagedConsumableKind, StringComparison.OrdinalIgnoreCase))
                {
                    return ConversionQuotaResult.Failure(
                        "购买失败：商品 " + ProductStoreId + " 的类型是 " + product.ProductKind +
                        "，但当前程序按 Store 托管易耗品读取 5 次余额。请在 Partner Center 确认该加载项类型是 Store-managed consumable。");
                }

                StorePurchaseResult purchaseResult = await product.RequestPurchaseAsync();
                switch (purchaseResult.Status)
                {
                    case StorePurchaseStatus.Succeeded:
                    case StorePurchaseStatus.AlreadyPurchased:
                        return await GetBalanceAsync("购买成功。");
                    case StorePurchaseStatus.NotPurchased:
                        if (purchaseResult.ExtendedError is not null)
                        {
                            return ConversionQuotaResult.Failure("购买未完成：" + FormatExtendedError(purchaseResult.ExtendedError));
                        }

                        return ConversionQuotaResult.Canceled("未完成购买。");
                    case StorePurchaseStatus.NetworkError:
                        return ConversionQuotaResult.Failure("购买失败：网络不可用。" + FormatOptionalExtendedError(purchaseResult.ExtendedError));
                    case StorePurchaseStatus.ServerError:
                        return ConversionQuotaResult.Failure("购买失败：Microsoft Store 服务暂时不可用。" + FormatOptionalExtendedError(purchaseResult.ExtendedError));
                    default:
                        return ConversionQuotaResult.Failure("购买失败：" + FormatExtendedError(purchaseResult.ExtendedError));
                }
            }
            catch (Exception exception)
            {
                return ConversionQuotaResult.Failure("购买失败：" + FormatExtendedError(exception));
            }
        }

        public async Task<ConversionQuotaResult> ConsumeOneAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return readinessFailure;
            }

            Guid trackingId = Guid.NewGuid();
            SavePendingConsumptions(LoadPendingConsumptions().Concat(new[] { trackingId }));
            return await FlushPendingConsumptionsAsync();
        }

        public async Task<ConversionQuotaPurchaseInfo> GetPurchaseInfoAsync()
        {
            ConversionQuotaResult? readinessFailure = GetStoreReadinessFailure();
            if (readinessFailure is not null)
            {
                return ConversionQuotaPurchaseInfo.Failure(readinessFailure.Message, readinessFailure.BalanceRemaining);
            }

            ConversionQuotaResult pendingResult = await FlushPendingConsumptionsAsync();
            if (!pendingResult.IsSuccess)
            {
                return ConversionQuotaPurchaseInfo.Failure(pendingResult.Message, pendingResult.BalanceRemaining);
            }

            ConversionQuotaResult balanceResult = await GetBalanceAsync();
            if (!balanceResult.IsSuccess)
            {
                return ConversionQuotaPurchaseInfo.Failure(balanceResult.Message, balanceResult.BalanceRemaining);
            }

            try
            {
                StoreProduct product = await GetQuotaProductAsync(GetStoreContext());
                var purchaseOption = new ConversionQuotaPurchaseOption(
                    ProductStoreId,
                    string.IsNullOrWhiteSpace(product.Title) ? "转换次数包" : product.Title,
                    string.IsNullOrWhiteSpace(product.Description) ? "音频处理额度" : product.Description,
                    product.Price?.FormattedPrice ?? "以 Microsoft Store 显示为准",
                    "5 次",
                    product.ProductKind);

                return ConversionQuotaPurchaseInfo.Success(
                    balanceResult.BalanceRemaining,
                    purchaseOption,
                    "额度信息已更新。");
            }
            catch (Exception exception)
            {
                return ConversionQuotaPurchaseInfo.Failure(
                    "无法读取可购买套餐：" + FormatExtendedError(exception),
                    balanceResult.BalanceRemaining);
            }
        }

        private async Task<ConversionQuotaResult> GetBalanceAsync(string successPrefix = "次数余额已更新。")
        {
            try
            {
                StoreContext context = GetStoreContext();
                StoreConsumableResult balanceResult = await context.GetConsumableBalanceRemainingAsync(ProductStoreId);
                if (balanceResult.Status == StoreConsumableStatus.Succeeded)
                {
                    return ConversionQuotaResult.Success(
                        balanceResult.BalanceRemaining,
                        successPrefix + " 剩余 " + balanceResult.BalanceRemaining + " 次。");
                }

                return ConversionQuotaResult.Failure(
                    "无法读取次数余额：" + FormatConsumableStatus(balanceResult.Status, balanceResult.ExtendedError),
                    balanceResult.BalanceRemaining);
            }
            catch (Exception exception)
            {
                return ConversionQuotaResult.Failure("无法读取次数余额：" + FormatExtendedError(exception));
            }
        }

        private async Task<ConversionQuotaResult> FlushPendingConsumptionsAsync()
        {
            List<Guid> pendingConsumptions = LoadPendingConsumptions();
            if (pendingConsumptions.Count == 0)
            {
                return ConversionQuotaResult.Success(null, "没有待同步的扣次。");
            }

            var remainingConsumptions = new List<Guid>();
            uint? latestBalance = null;

            try
            {
                StoreContext context = GetStoreContext();
                foreach (Guid trackingId in pendingConsumptions)
                {
                    StoreConsumableResult result = await context.ReportConsumableFulfillmentAsync(
                        ProductStoreId,
                        ConsumptionQuantity,
                        trackingId);

                    latestBalance = result.BalanceRemaining;
                    if (result.Status == StoreConsumableStatus.Succeeded)
                    {
                        continue;
                    }

                    remainingConsumptions.Add(trackingId);
                    SavePendingConsumptions(remainingConsumptions.Concat(pendingConsumptions.Skip(pendingConsumptions.IndexOf(trackingId) + 1)));
                    return ConversionQuotaResult.Failure(
                        "上次处理已完成，但扣次尚未同步：" + FormatConsumableStatus(result.Status, result.ExtendedError),
                        latestBalance);
                }

                SavePendingConsumptions(remainingConsumptions);
                return ConversionQuotaResult.Success(latestBalance, "扣次已同步。");
            }
            catch (Exception exception)
            {
                SavePendingConsumptions(pendingConsumptions);
                return ConversionQuotaResult.Failure("上次扣次尚未同步：" + FormatExtendedError(exception), latestBalance);
            }
        }

        private async Task<StoreProduct> GetQuotaProductAsync(StoreContext context)
        {
            string[] productKinds =
            {
                StoreManagedConsumableKind,
                UnmanagedConsumableKind,
                DurableKind
            };

            StoreProductQueryResult queryResult = await context.GetStoreProductsAsync(
                productKinds,
                new[] { ProductStoreId });

            if (queryResult.ExtendedError is not null)
            {
                throw new InvalidOperationException("无法读取 Microsoft Store 商品目录：" + FormatExtendedError(queryResult.ExtendedError));
            }

            if (queryResult.Products.TryGetValue(ProductStoreId, out StoreProduct product))
            {
                return product;
            }

            StoreProduct? matchingProduct = queryResult.Products.Values.FirstOrDefault(
                candidate => string.Equals(candidate.StoreId, ProductStoreId, StringComparison.OrdinalIgnoreCase));
            if (matchingProduct is not null)
            {
                return matchingProduct;
            }

            throw new InvalidOperationException(
                "Microsoft Store 没有返回商品 " + ProductStoreId +
                "。请确认该加载项已发布到当前市场，并且当前 Microsoft Store 登录账号可以购买。");
        }

        private StoreContext GetStoreContext()
        {
            if (_storeContext is null)
            {
                _storeContext = StoreContext.GetDefault();
            }

            if (!_isInitializedForWindow)
            {
                IntPtr windowHandle = _windowHandleProvider();
                if (windowHandle != IntPtr.Zero)
                {
                    _isInitializedForWindow = WinRtWindowInitializer.TryInitialize(_storeContext, windowHandle);
                }
            }

            return _storeContext;
        }

        private static ConversionQuotaResult? GetStoreReadinessFailure()
        {
            if (false && IsProcessElevated())
            {
                return ConversionQuotaResult.Failure("Microsoft Store 购买不能在管理员权限下运行。请用普通权限启动应用或 Visual Studio 后重试。");
            }

            string? packageIdentityError = GetPackageIdentityError();
            if (packageIdentityError is not null)
            {
                return ConversionQuotaResult.Failure(packageIdentityError);
            }

            return null;
        }

        private static string? GetPackageIdentityError()
        {
            try
            {
                string packageName = Package.Current.Id.Name;
                if (string.Equals(packageName, ExpectedPackageIdentityName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return "当前运行包身份是 " + packageName + "，不是 Microsoft Store 应用 " + ExpectedPackageIdentityName + "。请从 Store 安装包或 AudioConvert.Package 启动。";
            }
            catch (Exception exception)
            {
                return "当前程序没有 Microsoft Store/MSIX 包身份，不能发起内购。请从 Store 安装的应用或 AudioConvert.Package 启动。详细信息：" + FormatExtendedError(exception);
            }
        }

        private static bool IsProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    TokenElevation elevation;
                    int tokenInformationLength = Marshal.SizeOf(typeof(TokenElevation));
                    if (!GetTokenInformation(
                        identity.Token,
                        TokenInformationClass.TokenElevation,
                        out elevation,
                        tokenInformationLength,
                        out _))
                    {
                        return false;
                    }

                    return elevation.TokenIsElevated != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private List<Guid> LoadPendingConsumptions()
        {
            if (!File.Exists(_pendingConsumptionFilePath))
            {
                return new List<Guid>();
            }

            return File.ReadAllLines(_pendingConsumptionFilePath)
                .Select(line => Guid.TryParse(line, out Guid trackingId) ? trackingId : Guid.Empty)
                .Where(trackingId => trackingId != Guid.Empty)
                .Distinct()
                .ToList();
        }

        private void SavePendingConsumptions(IEnumerable<Guid> trackingIds)
        {
            string? directory = Path.GetDirectoryName(_pendingConsumptionFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string[] lines = trackingIds
                .Where(trackingId => trackingId != Guid.Empty)
                .Distinct()
                .Select(trackingId => trackingId.ToString("D"))
                .ToArray();

            if (lines.Length == 0)
            {
                if (File.Exists(_pendingConsumptionFilePath))
                {
                    File.Delete(_pendingConsumptionFilePath);
                }

                return;
            }

            File.WriteAllLines(_pendingConsumptionFilePath, lines);
        }

        private static string FormatConsumableStatus(StoreConsumableStatus status, Exception? extendedError)
        {
            switch (status)
            {
                case StoreConsumableStatus.NetworkError:
                    return "网络不可用。" + FormatOptionalExtendedError(extendedError);
                case StoreConsumableStatus.ServerError:
                    return "Microsoft Store 服务暂时不可用。" + FormatOptionalExtendedError(extendedError);
                case StoreConsumableStatus.InsufficentQuantity:
                    return "次数余额不足。" + FormatOptionalExtendedError(extendedError);
                default:
                    return FormatExtendedError(extendedError);
            }
        }

        private static string FormatExtendedError(Exception? extendedError)
        {
            if (extendedError is null)
            {
                return "未知错误。";
            }

            return extendedError.Message + " (0x" + extendedError.HResult.ToString("X8") + ")";
        }

        private static string FormatOptionalExtendedError(Exception? extendedError)
        {
            return extendedError is null ? string.Empty : " " + FormatExtendedError(extendedError);
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            TokenInformationClass tokenInformationClass,
            out TokenElevation tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        private enum TokenInformationClass
        {
            TokenElevation = 20
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenElevation
        {
            public int TokenIsElevated;
        }
    }
}
