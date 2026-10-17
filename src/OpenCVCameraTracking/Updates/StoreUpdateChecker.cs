using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.ApplicationModel;
using Windows.Services.Store;
using OpenCVCameraTracking.Core.Logging;

namespace OpenCVCameraTracking.Updates;

public sealed record StoreUpdateInfo(Version CurrentVersion, Version AvailableVersion, string StoreUrl);

public enum StoreUpdateCheckStatus
{
    UpdateAvailable,
    NoUpdate,
    NoPackageIdentity,
    Failed
}

public enum StoreUpdateCheckSource
{
    Startup,
    Timer,
    Manual
}

public sealed record StoreUpdateCheckResult(
    StoreUpdateCheckStatus Status,
    Version CurrentVersion,
    StoreUpdateInfo? Update,
    int UpdatePackageCount,
    IReadOnlyList<string> PackageVersions,
    string? ErrorMessage = null);

/// <summary>
/// Checks the Microsoft Store for package updates that are actually available to the
/// current signed-in account. This must be called from the application's UI thread.
/// </summary>
public sealed class StoreUpdateChecker
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const string ExpectedStorePackageName = "wuty.CameraTracking";
    private const string ExpectedStorePublisher = "CN=F045EE4F-387A-4525-ACD9-5C8EA792E1CE";
    public const string StoreProductId = "9P88GH39KPTD";
    public const string StoreProductUrl = "ms-windows-store://pdp/?productid=" + StoreProductId;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackagePath(ref uint packagePathLength, StringBuilder? packagePath);

    public async Task<StoreUpdateCheckResult> CheckAsync(
        StoreUpdateCheckSource source,
        CancellationToken cancellationToken = default)
    {
        var hasPackageIdentity = HasPackageIdentity();
        var current = GetAssemblyVersion();
        var currentVersionSource = "assembly-fallback";
        Package? currentPackage = null;
        PackageSignatureKind? signatureKind = null;
        string packageFullName = "unavailable";
        string packagePublisher = "unavailable";
        Exception? packageInfoException = null;
        if (hasPackageIdentity)
        {
            try
            {
                currentPackage = Package.Current;
                current = ToVersion(currentPackage.Id.Version);
                currentVersionSource = "package";
                signatureKind = currentPackage.SignatureKind;
                packageFullName = currentPackage.Id.FullName;
                packagePublisher = currentPackage.Id.Publisher;
            }
            catch (Exception exception)
            {
                packageInfoException = exception;
            }
        }

        var signatureKindText = signatureKind?.ToString() ?? "unavailable";
        AppLogger.Info(
            $"Microsoft Store update check started: source={source}, storeProductId={StoreProductId}, expectedPackageName={ExpectedStorePackageName}, expectedPublisher={ExpectedStorePublisher}, current={current}, currentVersionSource={currentVersionSource}, hasPackageIdentity={hasPackageIdentity}, signatureKind={signatureKindText}, packageFullName={packageFullName}, packagePublisher={packagePublisher}");

        if (!hasPackageIdentity)
        {
            AppLogger.Warn(
                $"Microsoft Store update check result: status={StoreUpdateCheckStatus.NoPackageIdentity}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, error=app-has-no-package-identity");
            return new StoreUpdateCheckResult(
                StoreUpdateCheckStatus.NoPackageIdentity,
                current,
                null,
                0,
                Array.Empty<string>(),
                "The running app has no package identity.");
        }

        if (packageInfoException is not null)
        {
            AppLogger.Error(
                $"Microsoft Store update check result: status={StoreUpdateCheckStatus.Failed}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, stage=read-package-identity, exceptionType={packageInfoException.GetType().FullName}, message={packageInfoException.Message}",
                packageInfoException);
            return new StoreUpdateCheckResult(
                StoreUpdateCheckStatus.Failed,
                current,
                null,
                0,
                Array.Empty<string>(),
                packageInfoException.Message);
        }

        if (!string.Equals(currentPackage!.Id.Name, ExpectedStorePackageName, StringComparison.Ordinal) ||
            !string.Equals(currentPackage.Id.Publisher, ExpectedStorePublisher, StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = $"Expected package identity '{ExpectedStorePackageName}' with publisher '{ExpectedStorePublisher}', but found '{currentPackage.Id.Name}' with publisher '{currentPackage.Id.Publisher}'.";
            AppLogger.Error(
                $"Microsoft Store update check result: status={StoreUpdateCheckStatus.Failed}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, signatureKind={signatureKindText}, packageFullName={packageFullName}, packagePublisher={packagePublisher}, error=package-identity-mismatch, message={errorMessage}");
            return new StoreUpdateCheckResult(
                StoreUpdateCheckStatus.Failed,
                current,
                null,
                0,
                Array.Empty<string>(),
                errorMessage);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = StoreContext.GetDefault();

            // The Store API is the source of truth: a package in Partner Center that is
            // still processing or in certification is not returned here.
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Version? available = null;
            var packageVersions = new List<string>();
            foreach (var update in updates)
            {
                var package = update.Package;
                if (package is null)
                {
                    continue;
                }

                var version = ToVersion(package.Id.Version);
                packageVersions.Add($"{package.Id.Name}={version}");
                if (version > current && (available is null || version > available))
                {
                    available = version;
                }
            }

            if (available is null)
            {
                AppLogger.Info(
                    $"Microsoft Store update check result: status={StoreUpdateCheckStatus.NoUpdate}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, signatureKind={signatureKindText}, packageFullName={packageFullName}, updatePackageCount={updates.Count}, packageVersions=[{FormatPackageVersions(packageVersions)}]");
                return new StoreUpdateCheckResult(
                    StoreUpdateCheckStatus.NoUpdate,
                    current,
                    null,
                    updates.Count,
                    packageVersions);
            }

            var updateInfo = new StoreUpdateInfo(current, available, StoreProductUrl);
            AppLogger.Info(
                $"Microsoft Store update check result: status={StoreUpdateCheckStatus.UpdateAvailable}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, signatureKind={signatureKindText}, packageFullName={packageFullName}, available={available}, updatePackageCount={updates.Count}, packageVersions=[{FormatPackageVersions(packageVersions)}]");
            return new StoreUpdateCheckResult(
                StoreUpdateCheckStatus.UpdateAvailable,
                current,
                updateInfo,
                updates.Count,
                packageVersions);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warn(
                $"Microsoft Store update check result: status=Cancelled, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, signatureKind={signatureKindText}, packageFullName={packageFullName}");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Error(
                $"Microsoft Store update check result: status={StoreUpdateCheckStatus.Failed}, source={source}, storeProductId={StoreProductId}, current={current}, currentVersionSource={currentVersionSource}, signatureKind={signatureKindText}, packageFullName={packageFullName}, exceptionType={exception.GetType().FullName}, message={exception.Message}",
                exception);
            return new StoreUpdateCheckResult(
                StoreUpdateCheckStatus.Failed,
                current,
                null,
                0,
                Array.Empty<string>(),
                exception.Message);
        }
    }

    private static string FormatPackageVersions(IEnumerable<string> packageVersions)
    {
        var values = packageVersions.ToArray();
        return values.Length == 0 ? "none" : string.Join(";", values);
    }

    public static Version GetCurrentVersion()
    {
        var packageVersion = TryGetCurrentPackageVersion();
        if (packageVersion is not null)
        {
            return packageVersion;
        }

        return GetAssemblyVersion();
    }

    private static Version GetAssemblyVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version ?? new Version(1, 0, 0, 0);
    }

    private static Version? TryGetCurrentPackageVersion()
    {
        // Unpackaged launches do not have Package.Current.  Check the package
        // identity through the AppModel API before accessing it.
        if (!HasPackageIdentity())
        {
            return null;
        }

        try
        {
            return ToVersion(Package.Current.Id.Version);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasPackageIdentity()
    {
        uint pathLength = 0;
        var result = GetCurrentPackagePath(ref pathLength, null);

        // A packaged process returns ERROR_INSUFFICIENT_BUFFER for the size-only
        // query.  An unpackaged process returns APPMODEL_ERROR_NO_PACKAGE.
        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        return result == ErrorInsufficientBuffer || result == 0;
    }

    private static Version ToVersion(PackageVersion version) =>
        new(version.Major, version.Minor, version.Build, version.Revision);
}
