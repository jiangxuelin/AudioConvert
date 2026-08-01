using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public sealed class ConvertResult
    {
        private ConvertResult(bool isSuccess, string? outputPath = null, string? errorMessage = null)
        {
            IsSuccess = isSuccess;
            OutputPath = outputPath;
            ErrorMessage = errorMessage;
        }

        public bool IsSuccess { get; }

        public string? OutputPath { get; }

        public string? ErrorMessage { get; }

        public static ConvertResult Success(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputPath));
            }

            return new ConvertResult(true, outputPath: outputPath);
        }

        public static ConvertResult Failure(string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(errorMessage));
            }

            return new ConvertResult(false, errorMessage: errorMessage);
        }
    }

    public sealed class NcmDecodeResult
    {
        private NcmDecodeResult(bool isSuccess, string? outputPath = null, string? format = null, string? errorMessage = null)
        {
            IsSuccess = isSuccess;
            OutputPath = outputPath;
            Format = format;
            ErrorMessage = errorMessage;
        }

        public bool IsSuccess { get; }

        public string? OutputPath { get; }

        public string? Format { get; }

        public string? ErrorMessage { get; }

        public static NcmDecodeResult Success(string outputPath, string format)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputPath));
            }

            if (string.IsNullOrWhiteSpace(format))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(format));
            }

            return new NcmDecodeResult(true, outputPath: outputPath, format: format);
        }

        public static NcmDecodeResult Failure(string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(errorMessage));
            }

            return new NcmDecodeResult(false, errorMessage: errorMessage);
        }
    }
}
