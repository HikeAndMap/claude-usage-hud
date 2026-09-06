using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeUsageHUD;

/// <summary>Draws a simple tray icon at runtime (a rounded orange circle with a white "C", echoing Claude's
/// own brand color) rather than embedding an .ico asset file - this app has exactly one icon, used in exactly
/// one place.</summary>
public static class TrayIconFactory
{
    public static Icon CreateIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(Color.FromArgb(255, 204, 120, 92));
            g.FillEllipse(brush, 2, 2, size - 4, size - 4);

            using var font = new Font("Segoe UI", 14f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("C", font, textBrush, new RectangleF(0, 0, size, size), format);
        }

        return CreateAlphaIcon(bmp);
    }

    /// <summary>
    /// Builds a real per-pixel-alpha icon via CreateIconIndirect, bypassing Bitmap.GetHicon() entirely -
    /// GetHicon() only supports a 1-bit transparency mask, so the anti-aliased edges SmoothingMode.AntiAlias
    /// produces (both the circle's outline and the text) came out as a plain gray blob in the tray, confirmed
    /// live 2026-09-06. A follow-up attempt hand-building an ICO container with a PNG-format frame (which
    /// Windows Explorer itself renders fine) turned out to not reliably load through System.Drawing.Icon
    /// either - the icon simply never appeared in the tray at all, not even as a placeholder, suggesting
    /// .NET's own ICO parser doesn't handle a PNG-compressed frame the same way the shell does. This approach
    /// avoids any ICO-file-format guessing: it builds the color bitmap as a real 32bpp DIB section (whose
    /// memory layout - BGRA bytes, top-down rows - already matches a locked Format32bppArgb Bitmap exactly,
    /// so the pixel data is a straight memory copy) plus an all-opaque 1bpp mask, then hands both directly to
    /// CreateIconIndirect - the same primitive Windows itself uses to build any real icon.
    /// </summary>
    private static Icon CreateAlphaIcon(Bitmap source)
    {
        int width = source.Width, height = source.Height;

        var bmi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>() - Marshal.SizeOf<int>(), // header size only, not the trailing color-table field
            biWidth = width,
            biHeight = -height, // negative = top-down DIB, matching a locked Bitmap's own row order
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr colorBitmap = IntPtr.Zero;
        IntPtr maskBitmap = IntPtr.Zero;
        try
        {
            colorBitmap = NativeMethods.CreateDIBSection(screenDc, ref bmi, 0 /* DIB_RGB_COLORS */,
                out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (colorBitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed.");

            BitmapData locked = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Format32bppArgb's in-memory byte order (B,G,R,A per pixel, on little-endian Windows) and
                // row order (top-down, since LockBits never flips a Bitmap's own natural layout) already
                // match exactly what the DIB section above expects - no per-pixel conversion needed, just a
                // straight copy. Both stride values are guaranteed width*4 for 32bpp with no row padding.
                int totalBytes = locked.Stride * height;
                byte[] pixelBytes = new byte[totalBytes];
                Marshal.Copy(locked.Scan0, pixelBytes, 0, totalBytes);
                Marshal.Copy(pixelBytes, 0, bitsPtr, totalBytes);
            }
            finally
            {
                source.UnlockBits(locked);
            }

            // 1bpp AND-mask, explicitly zeroed (= "opaque, trust the color bitmap's own alpha channel" under
            // the 32bpp-icon rendering rules modern Windows uses) - CreateBitmap leaves memory uninitialized
            // when passed a null bits pointer, so this is built explicitly rather than relying on that.
            int strideBytes = ((width + 31) / 32) * 4; // 1bpp rows are padded to a 4-byte boundary
            byte[] maskBytes = new byte[strideBytes * height];
            GCHandle maskHandle = GCHandle.Alloc(maskBytes, GCHandleType.Pinned);
            try
            {
                maskBitmap = NativeMethods.CreateBitmap(width, height, 1, 1, maskHandle.AddrOfPinnedObject());
            }
            finally
            {
                maskHandle.Free();
            }

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = colorBitmap,
                hbmMask = maskBitmap,
            };

            IntPtr hIcon = NativeMethods.CreateIconIndirect(ref iconInfo);
            if (hIcon == IntPtr.Zero) throw new InvalidOperationException("CreateIconIndirect failed.");

            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone(); // Clone owns its own copy, independent of hIcon
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
        finally
        {
            if (colorBitmap != IntPtr.Zero) NativeMethods.DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(maskBitmap);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int colors; // placeholder for the (unused, since biBitCount=32) trailing color-table entry
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
            out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr lpvBits);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
