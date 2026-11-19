using System;

namespace SvuSessionsLauncher.Linktivity;

/// <summary>
/// Student-side (viewer) port of the vendor's screen-share protocol
/// (<c>linktivity.viewer.ViewportProtocol</c> read path). Each VIEWPORT message
/// carries a decompressed command stream; this class consumes it and keeps the
/// running screen image. Viewport size, palette and pixel buffer persist across
/// messages, exactly as the vendor's instance fields do — a VIEWPORT_UPDATE is
/// sent once when sharing starts, and every later message is just BUFFER_UPDATE
/// tiles blitted into that buffer.
/// </summary>
public sealed class ViewportProtocol
{
    public const int BufferUpdate = 0;
    public const int PaletteUpdate = 2;
    public const int ViewportUpdate = 11;
    public const int UpdateRequest = 3;
    public const int MouseEvent = 4;
    public const int KeyEvent = 5;
    public const int ConfigRequest = 6;
    public const int Nop = 33;
    public const int ScaleChanged = 68;
    public const int Shutdown = 69;
    public const int RunDispatchQueue = 70;

    private readonly byte[] _palette = new byte[1024];
    private readonly int[] _mask = new int[3];
    private byte[] _pixels = Array.Empty<byte>();

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int BytesPerPixel { get; private set; }

    /// <summary>True once the host signalled the end of the share.</summary>
    public bool Stopped { get; private set; }

    /// <summary>True once a viewport geometry is known and pixels have been written.</summary>
    public bool HasFrame => Width > 0 && Height > 0 && _pixels.Length == Width * Height * BytesPerPixel && BytesPerPixel > 0;

    /// <summary>
    /// Consumes one decompressed command stream and updates the screen image.
    /// Returns <c>true</c> when the pixel buffer changed (i.e. a frame is ready
    /// to render).
    /// </summary>
    public bool Apply(byte[] stream)
    {
        if (stream == null || stream.Length == 0)
        {
            return false;
        }

        bool changed = false;
        int pos = 0;

        while (pos < stream.Length)
        {
            int type = stream[pos++];
            switch (type)
            {
                case ViewportUpdate:
                {
                    if (pos + 32 > stream.Length)
                    {
                        return changed;
                    }

                    Width = ReadInt(stream, pos);
                    Height = ReadInt(stream, pos + 4);
                    BytesPerPixel = ReadInt(stream, pos + 16) / 8;
                    _mask[0] = ReadInt(stream, pos + 20);
                    _mask[1] = ReadInt(stream, pos + 24);
                    _mask[2] = ReadInt(stream, pos + 28);

                    int needed = Width * Height * BytesPerPixel;
                    if (needed > 0 && _pixels.Length != needed)
                    {
                        _pixels = new byte[needed];
                    }

                    pos += 32;
                    break;
                }

                case PaletteUpdate:
                {
                    if (pos + _palette.Length > stream.Length)
                    {
                        return changed;
                    }

                    Array.Copy(stream, pos, _palette, 0, _palette.Length);
                    pos += _palette.Length;
                    changed = true;
                    break;
                }

                case BufferUpdate:
                {
                    if (pos + 20 > stream.Length)
                    {
                        return changed;
                    }

                    int x = ReadInt(stream, pos);
                    int y = ReadInt(stream, pos + 4);
                    int w = ReadInt(stream, pos + 8);
                    int h = ReadInt(stream, pos + 12);
                    int size = ReadInt(stream, pos + 16);
                    pos += 20;

                    if (size < 0 || pos + size > stream.Length)
                    {
                        return changed;
                    }

                    int rowBytes = w * BytesPerPixel;
                    bool fits = BytesPerPixel > 0
                        && w > 0
                        && h > 0
                        && size == rowBytes * h
                        && x >= 0
                        && y >= 0
                        && x + w <= Width
                        && y + h <= Height
                        && _pixels.Length == Width * Height * BytesPerPixel;

                    if (fits)
                    {
                        for (int row = 0; row < h; row++)
                        {
                            Array.Copy(
                                stream,
                                pos + row * rowBytes,
                                _pixels,
                                ((y + row) * Width + x) * BytesPerPixel,
                                rowBytes);
                        }

                        changed = true;
                    }

                    pos += size;
                    break;
                }

                case Nop:
                case ScaleChanged:
                case RunDispatchQueue:
                {
                    break;
                }

                case Shutdown:
                {
                    Stopped = true;
                    return changed;
                }

                default:
                {
                    // Unknown command: stop parsing this stream rather than
                    // mis-read the remainder.
                    return changed;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Converts the current screen image to a top-down 32-bit BGRA buffer
    /// (straight alpha, opaque), ready for a <c>PixelFormats.Bgra32</c> bitmap.
    /// </summary>
    public byte[] ToBgra32()
    {
        int w = Width;
        int h = Height;
        int bpp = BytesPerPixel;
        var dst = new byte[Math.Max(0, w * h * 4)];

        if (w <= 0 || h <= 0 || bpp <= 0)
        {
            return dst;
        }

        int count = w * h;

        if (bpp == 1)
        {
            for (int i = 0; i < count; i++)
            {
                int entry = _pixels[i] * 4;
                dst[i * 4 + 0] = _palette[entry + 0];
                dst[i * 4 + 1] = _palette[entry + 1];
                dst[i * 4 + 2] = _palette[entry + 2];
                dst[i * 4 + 3] = 0xFF;
            }
        }
        else if (bpp == 2)
        {
            int blueMask = _mask[2];
            int greenMask = _mask[1];
            int redMask = _mask[0];
            for (int i = 0; i < count; i++)
            {
                int v = _pixels[i * 2] | (_pixels[i * 2 + 1] << 8);
                dst[i * 4 + 0] = Scale(v, blueMask);
                dst[i * 4 + 1] = Scale(v, greenMask);
                dst[i * 4 + 2] = Scale(v, redMask);
                dst[i * 4 + 3] = 0xFF;
            }
        }
        else if (bpp == 3)
        {
            for (int i = 0; i < count; i++)
            {
                dst[i * 4 + 0] = _pixels[i * 3 + 0];
                dst[i * 4 + 1] = _pixels[i * 3 + 1];
                dst[i * 4 + 2] = _pixels[i * 3 + 2];
                dst[i * 4 + 3] = 0xFF;
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                dst[i * 4 + 0] = _pixels[i * 4 + 0];
                dst[i * 4 + 1] = _pixels[i * 4 + 1];
                dst[i * 4 + 2] = _pixels[i * 4 + 2];
                dst[i * 4 + 3] = 0xFF;
            }
        }

        return dst;
    }

    private static int ReadInt(byte[] source, int offset) =>
        (source[offset] & 0xFF)
        | ((source[offset + 1] & 0xFF) << 8)
        | ((source[offset + 2] & 0xFF) << 16)
        | ((source[offset + 3] & 0xFF) << 24);

    private static byte Scale(int value, int mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = 0;
        while ((mask & (1 << shift)) == 0 && shift < 31)
        {
            shift++;
        }

        int bits = 0;
        for (int bit = shift; bit < 32; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                bits++;
            }
        }

        if (bits == 0)
        {
            return 0;
        }

        int raw = (value & mask) >> shift;
        int max = (1 << bits) - 1;
        return (byte)((raw * 255 + max / 2) / max);
    }
}
