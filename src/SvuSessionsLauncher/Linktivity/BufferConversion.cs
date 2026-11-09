using System.Text;

namespace SvuSessionsLauncher.Linktivity;

public static class BufferConversion
{
    public static void IntToBytes(int value, byte[] target, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            target[offset + i] = (byte)(value & 0xFF);
            value = (int)((uint)value >> 8);
        }
    }

    public static int BytesToInt(byte[] source, int offset, int count)
    {
        int result = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            result <<= 8;
            result |= source[offset + i] & 0xFF;
        }
        return result;
    }

    public static void LongToBytes(long value, byte[] target, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            target[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    public static long BytesToLong(byte[] source, int offset, int count)
    {
        long result = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            result <<= 8;
            result |= (uint)(source[offset + i] & 0xFF);
        }
        return result;
    }

    public static byte[] GetUtf8(string value) => Encoding.UTF8.GetBytes(value);

    public static string GetUtf8(byte[] source, int offset, int length) =>
        Encoding.UTF8.GetString(source, offset, length);

    public static byte[] GetBytes(string value)
    {
        if (value == null)
        {
            return new byte[] { 0 };
        }
        byte[] utf8 = GetUtf8(value);
        byte[] result = new byte[utf8.Length + 1];
        Buffer.BlockCopy(utf8, 0, result, 0, utf8.Length);
        result[utf8.Length] = 0;
        return result;
    }

    public static byte[] GetBytes(string[] array, int offset)
    {
        if (array == null)
        {
            return new byte[offset];
        }
        byte[][] parts = new byte[array.Length][];
        int total = offset;
        for (int i = 0; i < array.Length; i++)
        {
            parts[i] = array[i] == null ? new byte[] { 0 } : GetBytes(array[i]);
            total += parts[i].Length;
        }
        byte[] result = new byte[total];
        int pos = offset;
        for (int i = 0; i < array.Length; i++)
        {
            Buffer.BlockCopy(parts[i], 0, result, pos, parts[i].Length);
            pos += parts[i].Length;
        }
        return result;
    }

    public static string[] GetStrings(byte[] source, int offset, int count)
    {
        string[] result = new string[count];
        int pos = offset;
        for (int i = 0; i < count; i++)
        {
            int start = pos;
            while (pos < source.Length && source[pos] != 0)
            {
                pos++;
            }
            result[i] = pos == start ? "" : GetUtf8(source, start, pos - start);
            pos++;
        }
        return result;
    }

    public static int GetStringLength(byte[] source, int offset)
    {
        int len = 0;
        while (offset + len < source.Length && source[offset + len] != 0)
        {
            len++;
        }
        return len;
    }

    public static string BytesToString(byte[] source, int offset) =>
        GetUtf8(source, offset, GetStringLength(source, offset));

    public static byte[] Slice(byte[] source, int offset, int length)
    {
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }
        byte[] result = new byte[length];
        Buffer.BlockCopy(source, offset, result, 0, length);
        return result;
    }

    public static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    public static byte[][] LongsToBytes(long[][] rows, int columns)
    {
        byte[][] result = new byte[rows.Length][];
        for (int r = 0; r < rows.Length; r++)
        {
            result[r] = new byte[8 * columns];
            for (int c = 0; c < columns; c++)
            {
                LongToBytes(rows[r][c], result[r], 8 * c, 8);
            }
        }
        return result;
    }

    public static long[][] BytesToLongs(byte[][] rows, int columns)
    {
        long[][] result = new long[rows.Length][];
        for (int r = 0; r < rows.Length; r++)
        {
            result[r] = new long[columns];
            for (int c = 0; c < columns; c++)
            {
                result[r][c] = BytesToLong(rows[r], 8 * c, 8);
            }
        }
        return result;
    }

    public static int WordToInt(byte[] source, int offset) =>
        ((source[offset + 1] & 0xFF) << 8) | (source[offset] & 0xFF);

    public static int TByteToInt(byte[] source, int offset) =>
        ((source[offset + 2] & 0xFF) << 16) | ((source[offset + 1] & 0xFF) << 8) | (source[offset] & 0xFF);

    public static bool Compare(byte[] first, byte[] second)
    {
        if (first == null || second == null || first.Length != second.Length)
        {
            return false;
        }
        for (int i = 0; i < first.Length; i++)
        {
            if (first[i] != second[i])
            {
                return false;
            }
        }
        return true;
    }

    public static string StringToBase64(string value) =>
        Convert.ToBase64String(GetUtf8(value));
}