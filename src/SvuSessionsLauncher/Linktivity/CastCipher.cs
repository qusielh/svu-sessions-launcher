using System;

namespace SvuSessionsLauncher.Linktivity;

/// <summary>
/// Port of the vendor's CAST-based block cipher. It is <b>not</b> TLS, and the
/// student client never enables it: <see cref="LinkConnection.EnableCipher"/>
/// and <see cref="LinkConnection.SetCipherKey"/> are not called, so session
/// traffic is effectively unencrypted. Kept for protocol completeness and
/// documented as a vendor limitation in <c>docs/security-privacy.md</c>.
/// </summary>
public class CastCipher
{
    private readonly byte[] _key = new byte[16];
    private bool _shortKey;
    private readonly int[] _subKey = new int[32];

    public CastCipher(byte[] key)
    {
        if (key != null)
        {
            SetKey(key);
            CreateKeyPairs();
        }
    }

    private void SetKey(byte[] key)
    {
        _shortKey = key.Length <= 10;
        Array.Clear(_key, 0, _key.Length);
        int count = key.Length >= 16 ? 16 : key.Length;
        Array.Copy(key, 0, _key, 0, count);
    }

    private static int Rotl(int value, int count)
    {
        count &= 31;
        if (count == 0)
        {
            return value;
        }
        return (value << count) | (int)((uint)value >> (32 - count));
    }

    private int CastType1(int d, int km, int kr)
    {
        int value = Rotl(km + d, kr);
        return (CipherTables.Box1[value >> 24 & 0xFF] ^ CipherTables.Box2[value >> 16 & 0xFF])
             - CipherTables.Box3[value >> 8 & 0xFF] + CipherTables.Box4[value & 0xFF];
    }

    private int CastType2(int d, int km, int kr)
    {
        int value = Rotl(km ^ d, kr);
        return (CipherTables.Box1[value >> 24 & 0xFF] - CipherTables.Box2[value >> 16 & 0xFF]
             + CipherTables.Box3[value >> 8 & 0xFF]) ^ CipherTables.Box4[value & 0xFF];
    }

    private int CastType3(int d, int km, int kr)
    {
        int value = Rotl(km - d, kr);
        return (CipherTables.Box1[value >> 24 & 0xFF] + CipherTables.Box2[value >> 16 & 0xFF]
             ^ CipherTables.Box3[value >> 8 & 0xFF]) - CipherTables.Box4[value & 0xFF];
    }

    public byte[] Encrypt(byte[] input, int length)
    {
        int remainder = length % 8;
        int fullLength = length - remainder;
        byte[] output = remainder == 0 ? new byte[length] : new byte[length + (9 - remainder)];
        int pos = 0;
        while (pos < fullLength)
        {
            EncryptBlock(input, pos, output, pos);
            pos += 8;
        }
        if (remainder > 0)
        {
            byte[] block = new byte[8];
            for (int i = 0; i < remainder; i++)
            {
                block[i] = input[pos + i];
            }
            EncryptBlock(block, 0, output, pos);
            output[output.Length - 1] = (byte)(9 - remainder);
        }
        return output;
    }

    public byte[] Decrypt(byte[] input, int length)
    {
        int remainder = length % 8;
        if (remainder > 0)
        {
            length += 8 - remainder;
        }
        if (length > input.Length)
        {
            length = input.Length;
        }
        byte[] output = new byte[length];
        int pos = 0;
        while (pos < length)
        {
            DecryptBlock(input, pos, output, pos);
            pos += 8;
        }
        return output;
    }

    public byte[] Decrypt(byte[] input, int length, bool padded)
    {
        int marker = padded ? input[length - 1] : 0;
        int targetLength = length - marker;
        byte[] output = new byte[targetLength];
        int pos = 0;
        int fullBoundary = marker > 0 ? targetLength - (9 - marker) : output.Length;
        if (fullBoundary < 0) fullBoundary = 0;
        while (pos < fullBoundary)
        {
            DecryptBlock(input, pos, output, pos);
            pos += 8;
        }
        if (marker > 0 && pos < targetLength)
        {
            byte[] block = new byte[8];
            DecryptBlock(input, pos, block, 0);
            for (int i = 0; i < 9 - marker; i++)
            {
                if (pos + i < output.Length)
                    output[pos + i] = block[i];
            }
        }
        return output;
    }

    private void EncryptBlock(byte[] input, int inputOffset, byte[] output, int outputOffset)
    {
        int var5 = (input[inputOffset] << 24 & unchecked((int)0xFF000000))
                 | (input[inputOffset + 1] << 16 & 0xFF0000)
                 | (input[inputOffset + 2] << 8 & 0xFF00)
                 | (input[inputOffset + 3] & 0xFF);
        int var6 = (input[inputOffset + 4] << 24 & unchecked((int)0xFF000000))
                 | (input[inputOffset + 5] << 16 & 0xFF0000)
                 | (input[inputOffset + 6] << 8 & 0xFF00)
                 | (input[inputOffset + 7] & 0xFF);
        int var7 = 0;
        int var8 = 16;
        int var9, var10;
        var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType2(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType3(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType2(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType3(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType2(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType3(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType2(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        var9 = var6; var10 = CastType3(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        if (!_shortKey)
        {
            var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
            var9 = var6; var10 = CastType2(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
            var9 = var6; var10 = CastType3(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
            var9 = var6; var10 = CastType1(var6, _subKey[var7], _subKey[var8]); var6 = var5 ^ var10; var5 = var9; var7++; var8++;
        }
        output[outputOffset] = (byte)(var6 >> 24 & 0xFF);
        output[outputOffset + 1] = (byte)(var6 >> 16 & 0xFF);
        output[outputOffset + 2] = (byte)(var6 >> 8 & 0xFF);
        output[outputOffset + 3] = (byte)(var6 & 0xFF);
        output[outputOffset + 4] = (byte)(var5 >> 24 & 0xFF);
        output[outputOffset + 5] = (byte)(var5 >> 16 & 0xFF);
        output[outputOffset + 6] = (byte)(var5 >> 8 & 0xFF);
        output[outputOffset + 7] = (byte)(var5 & 0xFF);
    }

    private void DecryptBlock(byte[] input, int inputOffset, byte[] output, int outputOffset)
    {
        int d = (input[inputOffset] << 24 & unchecked((int)0xFF000000))
              | (input[inputOffset + 1] << 16 & 0xFF0000)
              | (input[inputOffset + 2] << 8 & 0xFF00)
              | (input[inputOffset + 3] & 0xFF);
        int b = (input[inputOffset + 4] << 24 & unchecked((int)0xFF000000))
              | (input[inputOffset + 5] << 16 & 0xFF0000)
              | (input[inputOffset + 6] << 8 & 0xFF00)
              | (input[inputOffset + 7] & 0xFF);
        int var9 = _shortKey ? 11 : 15;
        int var10 = _shortKey ? 27 : 31;
        int var5, var6;
        if (!_shortKey)
        {
            var5 = b; var6 = CastType1(b, _subKey[var9], _subKey[var10]); b = d ^ var6; d = var5; var9--; var10--;
        }
        var5 = b; var6 = CastType3(b, _subKey[var9], _subKey[var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType2(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType1(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType3(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType2(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType1(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType3(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType2(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType1(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType3(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var5 = b; var6 = CastType2(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        var9--; var10--;
        if (_shortKey)
        {
            var5 = b; var6 = CastType1(b, _subKey[var9], _subKey[var10]); b = d ^ var6; d = var5;
        }
        else
        {
            var5 = b; var6 = CastType1(b, _subKey[var9], _subKey[var10]); b = d ^ var6; d = var5;
            var5 = b; var6 = CastType3(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
            var5 = b; var6 = CastType2(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
            var5 = b; var6 = CastType1(b, _subKey[--var9], _subKey[--var10]); b = d ^ var6; d = var5;
        }
        output[outputOffset] = (byte)(b >> 24 & 0xFF);
        output[outputOffset + 1] = (byte)(b >> 16 & 0xFF);
        output[outputOffset + 2] = (byte)(b >> 8 & 0xFF);
        output[outputOffset + 3] = (byte)(b & 0xFF);
        output[outputOffset + 4] = (byte)(d >> 24 & 0xFF);
        output[outputOffset + 5] = (byte)(d >> 16 & 0xFF);
        output[outputOffset + 6] = (byte)(d >> 8 & 0xFF);
        output[outputOffset + 7] = (byte)(d & 0xFF);
    }

    private void CreateKeyPairs()
    {
        int[] k = new int[8];
        k[0] = _key[0] << 24 & unchecked((int)0xFF000000) | (_key[1] << 16 & 0xFF0000) | (_key[2] << 8 & 0xFF00) | (_key[3] & 0xFF);
        k[1] = _key[4] << 24 & unchecked((int)0xFF000000) | (_key[5] << 16 & 0xFF0000) | (_key[6] << 8 & 0xFF00) | (_key[7] & 0xFF);
        k[2] = _key[8] << 24 & unchecked((int)0xFF000000) | (_key[9] << 16 & 0xFF0000) | (_key[10] << 8 & 0xFF00) | (_key[11] & 0xFF);
        k[3] = _key[12] << 24 & unchecked((int)0xFF000000) | (_key[13] << 16 & 0xFF0000) | (_key[14] << 8 & 0xFF00) | (_key[15] & 0xFF);
        k[4] = k[0] ^ CipherTables.Box5[k[3] >> 16 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        k[5] = k[2] ^ CipherTables.Box5[k[4] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] & 0xFF] ^ CipherTables.Box8[k[2] >> 8 & 0xFF];
        k[6] = k[3] ^ CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box5[k[2] >> 16 & 0xFF];
        k[7] = k[1] ^ CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[6] & 0xFF] ^ CipherTables.Box8[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF];
        _subKey[0] = CipherTables.Box5[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[5] & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box5[k[4] >> 8 & 0xFF];
        _subKey[1] = CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF];
        _subKey[2] = CipherTables.Box5[k[7] >> 24 & 0xFF] ^ CipherTables.Box6[k[7] >> 16 & 0xFF] ^ CipherTables.Box7[k[4] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[6] >> 16 & 0xFF];
        _subKey[3] = CipherTables.Box5[k[7] >> 8 & 0xFF] ^ CipherTables.Box6[k[7] & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] >> 24 & 0xFF] ^ CipherTables.Box8[k[7] >> 24 & 0xFF];
        k[0] = k[6] ^ CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] & 0xFF] ^ CipherTables.Box7[k[5] >> 24 & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 24 & 0xFF];
        k[1] = k[4] ^ CipherTables.Box5[k[0] >> 24 & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF];
        k[2] = k[5] ^ CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box5[k[4] >> 16 & 0xFF];
        k[3] = k[7] ^ CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[2] & 0xFF] ^ CipherTables.Box8[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] & 0xFF];
        _subKey[4] = CipherTables.Box5[k[0] & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 16 & 0xFF] ^ CipherTables.Box5[k[2] >> 24 & 0xFF];
        _subKey[5] = CipherTables.Box5[k[0] >> 16 & 0xFF] ^ CipherTables.Box6[k[0] >> 24 & 0xFF] ^ CipherTables.Box7[k[3] >> 8 & 0xFF] ^ CipherTables.Box8[k[3] & 0xFF] ^ CipherTables.Box6[k[3] >> 16 & 0xFF];
        _subKey[6] = CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF] ^ CipherTables.Box8[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[0] & 0xFF];
        _subKey[7] = CipherTables.Box5[k[1] >> 16 & 0xFF] ^ CipherTables.Box6[k[1] >> 24 & 0xFF] ^ CipherTables.Box7[k[2] >> 8 & 0xFF] ^ CipherTables.Box8[k[2] & 0xFF] ^ CipherTables.Box8[k[1] & 0xFF];
        k[4] = k[0] ^ CipherTables.Box5[k[3] >> 16 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        k[5] = k[2] ^ CipherTables.Box5[k[4] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] & 0xFF] ^ CipherTables.Box8[k[2] >> 8 & 0xFF];
        k[6] = k[3] ^ CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box5[k[2] >> 16 & 0xFF];
        k[7] = k[1] ^ CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[6] & 0xFF] ^ CipherTables.Box8[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF];
        _subKey[8] = CipherTables.Box5[k[4] & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[7] >> 24 & 0xFF] ^ CipherTables.Box8[k[7] >> 16 & 0xFF] ^ CipherTables.Box5[k[6] >> 16 & 0xFF];
        _subKey[9] = CipherTables.Box5[k[4] >> 16 & 0xFF] ^ CipherTables.Box6[k[4] >> 24 & 0xFF] ^ CipherTables.Box7[k[7] >> 8 & 0xFF] ^ CipherTables.Box8[k[7] & 0xFF] ^ CipherTables.Box6[k[7] >> 24 & 0xFF];
        _subKey[10] = CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[6] >> 24 & 0xFF] ^ CipherTables.Box8[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[4] >> 8 & 0xFF];
        _subKey[11] = CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] >> 24 & 0xFF] ^ CipherTables.Box7[k[6] >> 8 & 0xFF] ^ CipherTables.Box8[k[6] & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF];
        k[0] = k[6] ^ CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] & 0xFF] ^ CipherTables.Box7[k[5] >> 24 & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 24 & 0xFF];
        k[1] = k[4] ^ CipherTables.Box5[k[0] >> 24 & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF];
        k[2] = k[5] ^ CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box5[k[4] >> 16 & 0xFF];
        k[3] = k[7] ^ CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[2] & 0xFF] ^ CipherTables.Box8[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] & 0xFF];
        _subKey[12] = CipherTables.Box5[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[1] & 0xFF] ^ CipherTables.Box8[k[1] >> 8 & 0xFF] ^ CipherTables.Box5[k[0] & 0xFF];
        _subKey[13] = CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box6[k[1] & 0xFF];
        _subKey[14] = CipherTables.Box5[k[3] >> 24 & 0xFF] ^ CipherTables.Box6[k[3] >> 16 & 0xFF] ^ CipherTables.Box7[k[0] & 0xFF] ^ CipherTables.Box8[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        _subKey[15] = CipherTables.Box5[k[3] >> 8 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 16 & 0xFF];
        k[4] = k[0] ^ CipherTables.Box5[k[3] >> 16 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        k[5] = k[2] ^ CipherTables.Box5[k[4] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] & 0xFF] ^ CipherTables.Box8[k[2] >> 8 & 0xFF];
        k[6] = k[3] ^ CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box5[k[2] >> 16 & 0xFF];
        k[7] = k[1] ^ CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[6] & 0xFF] ^ CipherTables.Box8[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF];
        _subKey[16] = CipherTables.Box5[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[5] & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box5[k[4] >> 8 & 0xFF];
        _subKey[17] = CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF];
        _subKey[18] = CipherTables.Box5[k[7] >> 24 & 0xFF] ^ CipherTables.Box6[k[7] >> 16 & 0xFF] ^ CipherTables.Box7[k[4] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[6] >> 16 & 0xFF];
        _subKey[19] = CipherTables.Box5[k[7] >> 8 & 0xFF] ^ CipherTables.Box6[k[7] & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] >> 24 & 0xFF] ^ CipherTables.Box8[k[7] >> 24 & 0xFF];
        k[0] = k[6] ^ CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] & 0xFF] ^ CipherTables.Box7[k[5] >> 24 & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 24 & 0xFF];
        k[1] = k[4] ^ CipherTables.Box5[k[0] >> 24 & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF];
        k[2] = k[5] ^ CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box5[k[4] >> 16 & 0xFF];
        k[3] = k[7] ^ CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[2] & 0xFF] ^ CipherTables.Box8[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] & 0xFF];
        _subKey[20] = CipherTables.Box5[k[0] & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 16 & 0xFF] ^ CipherTables.Box5[k[2] >> 24 & 0xFF];
        _subKey[21] = CipherTables.Box5[k[0] >> 16 & 0xFF] ^ CipherTables.Box6[k[0] >> 24 & 0xFF] ^ CipherTables.Box7[k[3] >> 8 & 0xFF] ^ CipherTables.Box8[k[3] & 0xFF] ^ CipherTables.Box6[k[3] >> 16 & 0xFF];
        _subKey[22] = CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF] ^ CipherTables.Box8[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[0] & 0xFF];
        _subKey[23] = CipherTables.Box5[k[1] >> 16 & 0xFF] ^ CipherTables.Box6[k[1] >> 24 & 0xFF] ^ CipherTables.Box7[k[2] >> 8 & 0xFF] ^ CipherTables.Box8[k[2] & 0xFF] ^ CipherTables.Box8[k[1] & 0xFF];
        k[4] = k[0] ^ CipherTables.Box5[k[3] >> 16 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[3] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        k[5] = k[2] ^ CipherTables.Box5[k[4] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 16 & 0xFF] ^ CipherTables.Box8[k[4] & 0xFF] ^ CipherTables.Box8[k[2] >> 8 & 0xFF];
        k[6] = k[3] ^ CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[5] >> 16 & 0xFF] ^ CipherTables.Box8[k[5] >> 24 & 0xFF] ^ CipherTables.Box5[k[2] >> 16 & 0xFF];
        k[7] = k[1] ^ CipherTables.Box5[k[6] >> 8 & 0xFF] ^ CipherTables.Box6[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[6] & 0xFF] ^ CipherTables.Box8[k[6] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF];
        _subKey[24] = CipherTables.Box5[k[4] & 0xFF] ^ CipherTables.Box6[k[4] >> 8 & 0xFF] ^ CipherTables.Box7[k[7] >> 24 & 0xFF] ^ CipherTables.Box8[k[7] >> 16 & 0xFF] ^ CipherTables.Box5[k[6] >> 16 & 0xFF];
        _subKey[25] = CipherTables.Box5[k[4] >> 16 & 0xFF] ^ CipherTables.Box6[k[4] >> 24 & 0xFF] ^ CipherTables.Box7[k[7] >> 8 & 0xFF] ^ CipherTables.Box8[k[7] & 0xFF] ^ CipherTables.Box6[k[7] >> 24 & 0xFF];
        _subKey[26] = CipherTables.Box5[k[5] & 0xFF] ^ CipherTables.Box6[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[6] >> 24 & 0xFF] ^ CipherTables.Box8[k[6] >> 16 & 0xFF] ^ CipherTables.Box7[k[4] >> 8 & 0xFF];
        _subKey[27] = CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] >> 24 & 0xFF] ^ CipherTables.Box7[k[6] >> 8 & 0xFF] ^ CipherTables.Box8[k[6] & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF];
        k[0] = k[6] ^ CipherTables.Box5[k[5] >> 16 & 0xFF] ^ CipherTables.Box6[k[5] & 0xFF] ^ CipherTables.Box7[k[5] >> 24 & 0xFF] ^ CipherTables.Box8[k[5] >> 8 & 0xFF] ^ CipherTables.Box7[k[4] >> 24 & 0xFF];
        k[1] = k[4] ^ CipherTables.Box5[k[0] >> 24 & 0xFF] ^ CipherTables.Box6[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] & 0xFF] ^ CipherTables.Box8[k[4] >> 8 & 0xFF];
        k[2] = k[5] ^ CipherTables.Box5[k[1] & 0xFF] ^ CipherTables.Box6[k[1] >> 8 & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box5[k[4] >> 16 & 0xFF];
        k[3] = k[7] ^ CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[2] & 0xFF] ^ CipherTables.Box8[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[4] & 0xFF];
        _subKey[28] = CipherTables.Box5[k[2] >> 24 & 0xFF] ^ CipherTables.Box6[k[2] >> 16 & 0xFF] ^ CipherTables.Box7[k[1] & 0xFF] ^ CipherTables.Box8[k[1] >> 8 & 0xFF] ^ CipherTables.Box5[k[0] & 0xFF];
        _subKey[29] = CipherTables.Box5[k[2] >> 8 & 0xFF] ^ CipherTables.Box6[k[2] & 0xFF] ^ CipherTables.Box7[k[1] >> 16 & 0xFF] ^ CipherTables.Box8[k[1] >> 24 & 0xFF] ^ CipherTables.Box6[k[1] & 0xFF];
        _subKey[30] = CipherTables.Box5[k[3] >> 24 & 0xFF] ^ CipherTables.Box6[k[3] >> 16 & 0xFF] ^ CipherTables.Box7[k[0] & 0xFF] ^ CipherTables.Box8[k[0] >> 8 & 0xFF] ^ CipherTables.Box7[k[2] >> 24 & 0xFF];
        _subKey[31] = CipherTables.Box5[k[3] >> 8 & 0xFF] ^ CipherTables.Box6[k[3] & 0xFF] ^ CipherTables.Box7[k[0] >> 16 & 0xFF] ^ CipherTables.Box8[k[0] >> 24 & 0xFF] ^ CipherTables.Box8[k[3] >> 16 & 0xFF];
    }
}
