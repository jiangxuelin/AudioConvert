#if PACKAGE_TEST_QUOTA
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public sealed class PackageTestConversionQuotaService : IConversionQuotaService
    {
        private const uint TestBalance = 9999;

        public Task<ConversionQuotaResult> SignInAsync()
        {
            return Task.FromResult(
                ConversionQuotaResult.Success(TestBalance, "Package test quota bypass is enabled."));
        }

        public Task<ConversionQuotaResult> RefreshBalanceAsync()
        {
            return Task.FromResult(
                ConversionQuotaResult.Success(TestBalance, "Package test quota bypass is enabled."));
        }

        public Task<ConversionQuotaResult> EnsureQuotaAsync()
        {
            return Task.FromResult(
                ConversionQuotaResult.Success(TestBalance, "Package test quota bypass is enabled."));
        }

        public Task<ConversionQuotaResult> PurchaseQuotaAsync()
        {
            return Task.FromResult(
                ConversionQuotaResult.Success(TestBalance, "Package test quota bypass is enabled."));
        }

        public Task<ConversionQuotaResult> ConsumeOneAsync()
        {
            return Task.FromResult(
                ConversionQuotaResult.Success(TestBalance, "Package test quota bypass is enabled."));
        }
    }
}
#endif
