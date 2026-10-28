using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CouchHud.Shell.Services;

/// <summary>
/// One picture of one monitor, for the screenshot on the controller. First through DXGI desktop duplication (what the compositor
/// shows, also full-screen games in flip mode that leave GDI with a black picture), then through GDI BitBlt as the fallback.
/// Every COM pointer, GDI handle and the mapped texture are released in finally blocks; nothing is kept between screenshots.
/// </summary>
public static unsafe class ScreenCapture
{
    /// <summary>The pixels in BGRA (alpha ignored), top row first, stride Width * 4.</summary>
    public sealed record Frame(int Width, int Height, byte[] Pixels, string Method)
    {
        /// <summary>Only black (a game or video that keeps itself out of captures, or a capture that saw nothing).</summary>
        public bool AllBlack()
        {
            var p = Pixels;
            for (var i = 0; i < p.Length; i += 4)
            {
                if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 0) return false;
            }
            return true;
        }
    }

    /// <summary>The monitor the window in front is on (the game, or CouchHUD itself), the primary one when there is no window.</summary>
    public static IntPtr MonitorInFront()
    {
        const uint DefaultToPrimary = 1;
        return MonitorFromWindow(GetForegroundWindow(), DefaultToPrimary);
    }

    /// <summary>A picture of the monitor; desktop duplication first, GDI if that fails or returns only black.</summary>
    /// <exception cref="InvalidOperationException">Neither way worked.</exception>
    public static Frame Capture(IntPtr monitor)
    {
        Frame? dxgi = null;
        try
        {
            dxgi = Duplication.Capture(monitor);
            if (dxgi is not null && !dxgi.AllBlack()) return dxgi;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[screenshot] desktop duplication: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        try
        {
            var gdi = Gdi.Capture(monitor);
            // Both black: the duplication's picture is as good as any (the caller says it honestly).
            return gdi.AllBlack() && dxgi is not null ? dxgi : gdi;
        }
        catch (InvalidOperationException) when (dxgi is not null)
        {
            return dxgi;
        }
    }

    /// <summary>The frame as a PNG; the alpha channel is dropped (the desktop's alpha says nothing).</summary>
    public static BitmapSource ToBitmap(Frame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void WritePng(BitmapSource bitmap, Stream stream)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>A small copy for the toast (frozen, usable on any thread).</summary>
    public static BitmapSource Thumbnail(BitmapSource bitmap, int width = 256)
    {
        var scale = Math.Min(1.0, width / (double)Math.Max(1, bitmap.PixelWidth));
        var small = new WriteableBitmap(new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)));
        small.Freeze();
        return small;
    }

    // -----------------------------------------------------------------------
    // DXGI desktop duplication through the raw vtables (no wrapper library). Indices from the Windows SDK headers
    // (dxgi.h, dxgi1_2.h, d3d11.h), counted with IUnknown's three methods first.

    private static class Duplication
    {
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        private const uint FormatB8G8R8A8 = 87;
        private const uint UsageStaging = 3;
        private const uint CpuAccessRead = 0x20000;
        private const uint MapRead = 1;
        private const uint CreateDeviceBgraSupport = 0x20;
        private const uint SdkVersion = 7;

        private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedSubresource
        {
            public byte* Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameInfo
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        // Never a dxgi.dll or d3d11.dll from the application folder or the working directory (audit 0.5): System32 only.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        private static void** Vtbl(IntPtr obj) => *(void***)obj;

        private static void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[2])(obj);
            obj = IntPtr.Zero;
        }

        private static int QueryInterface(IntPtr obj, in Guid iid, out IntPtr result)
        {
            IntPtr found;
            fixed (Guid* g = &iid)
            {
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(obj)[0])(obj, g, &found);
                result = hr >= 0 ? found : IntPtr.Zero;
                return hr;
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
        }

        public static Frame? Capture(IntPtr monitor)
        {
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero;
            try
            {
                Check(CreateDXGIFactory1(IidFactory1, out factory), "CreateDXGIFactory1");
                // DXGI_OUTPUT_DESC: DeviceName[32] (64 bytes), RECT (16), AttachedToDesktop (4), Rotation (4), HMONITOR (8).
                var desc = stackalloc byte[96];
                // Every adapter: on a laptop with two GPUs the TV may hang on the other one.
                for (uint a = 0; ; a++)
                {
                    IntPtr nextAdapter;
                    var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory)[12])(factory, a, &nextAdapter); // EnumAdapters1
                    if (hr == DxgiErrorNotFound) return null;
                    Check(hr, "EnumAdapters1");
                    adapter = nextAdapter;
                    for (uint o = 0; ; o++)
                    {
                        IntPtr nextOutput;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(adapter)[7])(adapter, o, &nextOutput); // EnumOutputs
                        if (hr == DxgiErrorNotFound) break;
                        Check(hr, "EnumOutputs");
                        output = nextOutput;
                        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Vtbl(output)[7])(output, desc), "IDXGIOutput.GetDesc"); // GetDesc
                        var outputMonitor = *(IntPtr*)(desc + 88);
                        var rotation = *(uint*)(desc + 84);
                        if (outputMonitor == monitor)
                        {
                            // A turned display comes unturned from the duplication: GDI gets that right.
                            if (rotation > 1) throw new NotSupportedException($"rotated output ({rotation})");
                            return CaptureOutput(adapter, output);
                        }
                        Release(ref output);
                    }
                    Release(ref adapter);
                }
            }
            finally
            {
                Release(ref output);
                Release(ref adapter);
                Release(ref factory);
            }
        }

        private static Frame CaptureOutput(IntPtr adapter, IntPtr output)
        {
            IntPtr device = IntPtr.Zero, context = IntPtr.Zero, output1 = IntPtr.Zero, duplication = IntPtr.Zero, staging = IntPtr.Zero;
            try
            {
                // With an adapter given the driver type must be UNKNOWN (0).
                Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out device, out _, out context), "D3D11CreateDevice");
                Check(QueryInterface(output, IidOutput1, out output1), "IDXGIOutput1");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(output1)[22])(output1, device, &duplication), "DuplicateOutput"); // DuplicateOutput

                Texture2DDesc desc = default;
                var copied = false;
                // The first frame is the desktop as it is; a second one within a moment is the newer picture of a running game.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var got = AcquireInto(device, context, duplication, ref staging, ref desc, attempt == 0 ? 500u : 100u, out var presented);
                    if (got) copied = true;
                    if (!got || presented) break;
                }
                if (!copied) throw new InvalidOperationException("no frame from the desktop duplication");

                MappedSubresource mapped;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Vtbl(context)[14])(context, staging, 0, MapRead, 0, &mapped), "Map"); // Map
                try
                {
                    int width = (int)desc.Width, height = (int)desc.Height, stride = width * 4;
                    var pixels = new byte[(long)stride * height];
                    fixed (byte* dest = pixels)
                    {
                        for (var y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(mapped.Data + (long)y * mapped.RowPitch, dest + (long)y * stride, stride, stride);
                        }
                    }
                    return new Frame(width, height, pixels, "dxgi");
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(context)[15])(context, staging, 0); // Unmap
                }
            }
            finally
            {
                Release(ref staging);
                Release(ref duplication);
                Release(ref output1);
                Release(ref context);
                Release(ref device);
            }
        }

        /// <summary>One frame into the staging texture (made on the first frame). False on a timeout.</summary>
        private static bool AcquireInto(IntPtr device, IntPtr context, IntPtr duplication, ref IntPtr staging, ref Texture2DDesc desc, uint timeoutMs, out bool presented)
        {
            presented = false;
            FrameInfo info;
            IntPtr resource = IntPtr.Zero, texture = IntPtr.Zero;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Vtbl(duplication)[8])(duplication, timeoutMs, &info, &resource); // AcquireNextFrame
            if (hr == DxgiErrorWaitTimeout) return false;
            Check(hr, "AcquireNextFrame");
            try
            {
                Check(QueryInterface(resource, IidTexture2D, out texture), "ID3D11Texture2D");
                if (staging == IntPtr.Zero)
                {
                    Texture2DDesc source;
                    ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Vtbl(texture)[10])(texture, &source); // GetDesc
                    if (source.Format != FormatB8G8R8A8) throw new NotSupportedException($"desktop format {source.Format}");
                    desc = source with
                    {
                        MipLevels = 1, ArraySize = 1, SampleCount = 1, SampleQuality = 0,
                        Usage = UsageStaging, BindFlags = 0, CpuAccessFlags = CpuAccessRead, MiscFlags = 0
                    };
                    var d = desc;
                    IntPtr created;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Vtbl(device)[5])(device, &d, IntPtr.Zero, &created), "CreateTexture2D"); // CreateTexture2D
                    staging = created;
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(context)[47])(context, staging, texture); // CopyResource
                presented = info.LastPresentTime != 0;
                return true;
            }
            finally
            {
                Release(ref texture);
                Release(ref resource);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(duplication)[14])(duplication); // ReleaseFrame
            }
        }
    }

    // -----------------------------------------------------------------------
    // GDI: BitBlt of the monitor's rectangle from the screen.

    private static class Gdi
    {
        private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
        private static readonly IntPtr PerMonitorAwareV2 = new(-4);

        /// <summary>
        /// Monitor rectangle and BitBlt in physical pixels: this (pool) thread is per-monitor aware for the capture, whatever the
        /// process says, and gets its old awareness back afterwards. Without it a scaled TV (150 %) comes out cut or blurred.
        /// </summary>
        public static Frame Capture(IntPtr monitor)
        {
            IntPtr before = IntPtr.Zero;
            try
            {
                before = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            }
            catch (EntryPointNotFoundException)
            {
                // Before Windows 10 1607: the process's awareness has to do.
            }
            try
            {
                return CaptureAware(monitor);
            }
            finally
            {
                if (before != IntPtr.Zero) SetThreadDpiAwarenessContext(before);
            }
        }

        private static Frame CaptureAware(IntPtr monitor)
        {
            var info = new MonitorInfo { Size = sizeof(MonitorInfo) };
            if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("GetMonitorInfo failed");
            var r = info.Monitor;
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidOperationException("odd monitor size");

            IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) throw new InvalidOperationException("GetDC failed");
                memory = CreateCompatibleDC(screen);
                if (memory == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
                // Top-down 32-bit DIB: rows come out in the order PNG wants them.
                var header = new BitmapInfoHeader { Size = sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
                bitmap = CreateDIBSection(memory, ref header, 0, out var bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                previous = SelectObject(memory, bitmap);
                if (!BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, SrcCopy | CaptureBlt)) throw new InvalidOperationException("BitBlt failed");
                var pixels = new byte[(long)width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                return new Frame(width, height, pixels, "gdi");
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}
