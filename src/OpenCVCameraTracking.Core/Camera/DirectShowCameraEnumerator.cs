using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OpenCVCameraTracking.Core.Camera;

public static class DirectShowCameraEnumerator
{
    private static readonly Guid VideoInputDeviceCategory =
        new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    public static IReadOnlyList<CameraDeviceInfo> GetVideoInputDevices()
    {
        var devices = new List<CameraDeviceInfo>();
        ICreateDevEnum? deviceEnumerator = null;
        IEnumMoniker? monikerEnumerator = null;

        try
        {
            deviceEnumerator = (ICreateDevEnum)(object)new SystemDeviceEnum();
            var category = VideoInputDeviceCategory;
            var result = deviceEnumerator.CreateClassEnumerator(ref category, out monikerEnumerator, 0);
            if (result != 0 || monikerEnumerator is null)
            {
                return devices;
            }

            var monikers = new IMoniker[1];
            var index = 0;
            while (monikerEnumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var propertyBagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null, ref propertyBagId, out var bagObject);
                    var propertyBag = (IPropertyBag)bagObject;
                    try
                    {
                        object value = string.Empty;
                        var name = propertyBag.Read("FriendlyName", ref value, IntPtr.Zero) == 0
                            ? Convert.ToString(value) ?? $"摄像头 {index}"
                            : $"摄像头 {index}";
                        devices.Add(new CameraDeviceInfo(index++, name));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(propertyBag);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch (COMException)
        {
            // Some virtual-camera drivers expose incomplete COM metadata. Returning
            // the devices discovered so far is more useful than failing enumeration.
        }
        finally
        {
            if (monikerEnumerator is not null)
            {
                Marshal.ReleaseComObject(monikerEnumerator);
            }

            if (deviceEnumerator is not null)
            {
                Marshal.ReleaseComObject(deviceEnumerator);
            }
        }

        return devices;
    }

    [ComImport]
    [Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86")]
    private sealed class SystemDeviceEnum;

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            [In] ref Guid category,
            out IEnumMoniker? enumMoniker,
            int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value,
            IntPtr errorLog);

        [PreserveSig]
        int Write(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
