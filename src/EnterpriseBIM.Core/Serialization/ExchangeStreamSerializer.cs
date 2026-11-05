using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseBIM.Core.Contracts;

namespace EnterpriseBIM.Core.Serialization
{
    public static class ExchangeStreamSerializer
    {
        private const string GlobalMutexName = @"Global\EnterpriseBIM_ExchangeFileMutex_v1";
        private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string GetEnterpriseStagingFilePath()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string directory = Path.Combine(programData, "EnterpriseBIM");
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            return Path.Combine(directory, "exchange.json");
        }

        public static string GetEnterpriseAuditLogFilePath()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string directory = Path.Combine(programData, "EnterpriseBIM", "Logs");
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            return Path.Combine(directory, $"enterprisebim_{DateTime.UtcNow:yyyyMMdd}.log");
        }

        public static async Task WritePackageAsync(
            ExchangePackage package,
            string? targetFilePath = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(package);

            string resolvedPath = string.IsNullOrWhiteSpace(targetFilePath)
                ? GetEnterpriseStagingFilePath()
                : Path.GetFullPath(targetFilePath);

            string? directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ExchangePackage signedPackage = package with
            {
                PayloadChecksumSha256 = package.ComputeDeterministicHash()
            };

            string tempFilePath = $"{resolvedPath}.{Guid.NewGuid():N}.tmp";

            using Mutex mutex = new(false, GlobalMutexName);
            bool hasHandle = false;

            try
            {
                try
                {
                    hasHandle = mutex.WaitOne(DefaultLockTimeout, false);
                }
                catch (AbandonedMutexException)
                {
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    throw new TimeoutException($"Timed out waiting for exclusive lock on '{resolvedPath}'.");
                }

                await using (FileStream tempStream = new(
                    tempFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(tempStream, signedPackage, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempFilePath, resolvedPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { /* Best-effort temp cleanup */ }
                }

                if (hasHandle)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        public static ExchangePackage ReadPackageSynchronously(string? sourceFilePath = null)
        {
            string resolvedPath = string.IsNullOrWhiteSpace(sourceFilePath)
                ? GetEnterpriseStagingFilePath()
                : Path.GetFullPath(sourceFilePath);

            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException("EnterpriseBIM staging payload file does not exist.", resolvedPath);
            }

            using Mutex mutex = new(false, GlobalMutexName);
            bool hasHandle = false;

            try
            {
                try
                {
                    hasHandle = mutex.WaitOne(DefaultLockTimeout, false);
                }
                catch (AbandonedMutexException)
                {
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    throw new TimeoutException($"Timed out waiting for read access on '{resolvedPath}'.");
                }

                using FileStream stream = new(
                    resolvedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 65536,
                    useAsync: false);

                ExchangePackage? package = JsonSerializer.Deserialize<ExchangePackage>(stream, SerializerOptions)
                    ?? throw new InvalidDataException("Deserialized ExchangePackage evaluated to null.");

                string expectedChecksum = package.ComputeDeterministicHash();
                if (!string.Equals(package.PayloadChecksumSha256, expectedChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"ExchangePackage checksum verification failed. Expected '{expectedChecksum}', received '{package.PayloadChecksumSha256}'.");
                }

                return package;
            }
            finally
            {
                if (hasHandle)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        public static void AppendAuditLog(string component, string severity, string message)
        {
            string logFilePath = GetEnterpriseAuditLogFilePath();
            string logLine = $"[{DateTimeOffset.UtcNow:O}] [{component}] [{severity}] {message}{Environment.NewLine}";
            lock (SerializerOptions)
            {
                File.AppendAllText(logFilePath, logLine, Encoding.UTF8);
            }
        }
    }
}
