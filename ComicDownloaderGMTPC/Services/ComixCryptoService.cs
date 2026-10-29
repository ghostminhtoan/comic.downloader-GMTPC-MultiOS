using System;
using System.Text;

namespace ComicDownloaderGMTPC.Services;

public static class ComixCryptoService
{
    private static readonly uint[] Hj =
    [
        1116352408, 1899447441, 3049323471, 3921009573, 961987163, 1508970993, 2453635748, 2870763221,
        3624381080, 310598401, 607225278, 1426881987, 1925078388, 2162078206, 2614888103, 3248222580,
        3835390401, 4022224774, 264347078, 604807628, 770255983, 1249150122, 1555081692, 1996064986,
        2554220882, 2821834349, 2952996808, 3210313671, 3336571891, 3584528711, 113926993, 338241895,
        666307205, 773529912, 1294757372, 1396182291, 1695183700, 1986661051, 2177026350, 2456956037,
        2730485921, 2820302411, 3259730800, 3345764771, 3516065817, 3600352804, 4094571909, 275423344,
        430227734, 506948616, 659060556, 883997877, 958139571, 1322822218, 1537002063, 1747873779,
        1955562222, 2024104815, 2227730452, 2361852424, 2428436474, 2756734187, 3204031479, 3329325298
    ];

    private static readonly char[] Nj = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray();

    private const string Base64KeyTable =
        "1Gay3iETkw7Yj+9e2LrEDFNWkTAPj6S3g2jZ3k+ZCh5P+LT+FCcycNE9E9Si8iis6WcRmyFBsu36KaECDLePW1MNYoHOCYt1cpOkcofWJV4MhRlMrbIn3cecLJ0dKqkAvK+dzANZQMVDk35zDHbTv6CD9E2ROUeh4NTf3yPHV6YNYDpzFWxs/TsGxJoGnpWCv+ayx6sO+nhXCXTnBIVKrX6zL8O02EglrsmIYpC/+XkQUn3m2Aw20KEqXXej/dCfSlOReHqyhlxBefkf2uz80QyXqGOwI7ZQJDxtdtXAtmjo+S1Kiaxhwvw8ow+Wu3iBy+9XwtSP78kmrHLNu8GFlu3P5sVmq6Cr6vkZ3y6EAEO+zl45xHce3mTeV1A21FNO1wpdAtmifEK1rnfQwz7FhBduVheITjd0sXsEb+E/VqYYoVLFMdCqll0VfqrMarwaC9IW2/FSAbc5KB6ceGM1GBvjkvg5JpgeSaDIKnNheNfaVZxdg3DgbnD3eEHkOuw0+eBcTrgXwXp3lgWRVaQ34uvK4VVQcxpqW9QJOBKszYPJeFmTqpgo7TDK1SFYO5YU/jiDOC9OEk4iAKDes1U9LoU1RcuzTyHHdiWhMF9spTXJ/jbb5NL3Hj6rp3qL+PN01LpzDPnIq2yKwDQiHWVhJEEdrfyesfSfMp6yNRanrc3uRUhDsVFZ1ETS9m9UkBhM2kfz//xN1Fa40i2izqZc3K5ORBOaNblXenn4rga0EE5oFxfd1fHcqQoWi8W4Rh11xXitB1VcNnh4+bf9HVefXsgs8LGnKJWkEUkLCH9/iDgGQjbmGXhEJkiqI9CvltXrdVB9e3HvVg6L80OPKEkDoxkyX+PD0wV0RY1CiQDV9VYUleafBnnThJQfrwyMRkfmBzMBotrdoQ9JhpBwECsYxs7F9dWQeuNp8vpm9uM3KUgLdpSzDGWW2IuooeGhO26w7NLMsJfG2afXzC6jh9Xpyg7hyyzUDONSAfQ7DLIkshnmUe+Kkr5szxGrLStXjaMIo9/cc7PsJfQ2yzL7OzycIipauc1foN4TplU6MdDFx5hC8mdPNfYOQwdiExNawGbIb6FPeGQ1wUeT+Bzi+13naPCoqiajDOfIrDssSBdkBzbB1OvsXgUawqJdJ4bWPMZ4fupLWg+hoKuZCBkz/rYTsYKrFnJs1ENIAv1gQVgkXWNOdB2bqrySz18aEh7RTG0H+Fqp43yVSEGNnFwH4EAsLyDjA0OUxLKhP8UMEO292R7+XQJySlX0dvTBOKBmUChZPAK3D+6mVIDwxSxtSYeEfMXwa/IeZmwDlru1ITY3XH3o0U7AGF3EueBPj4/jPu7rax+7GHG7+1+BMinZEDeWpZhhlmwKSaZEEWT6/ZKu3CFBrfy3vrLXphoeCDL/xGO+HvO+BSu3b/4uud8WZGrBhnVVn7SPq2+zvZZQnkUEdwc=";

    private static readonly byte[] KeyTable = Convert.FromBase64String(Base64KeyTable);

    private static uint RotateRight(uint val, int count) => (val >> count) | (val << (32 - count));

    private static void Jj(uint[] W, byte[] q, int E)
    {
        uint[] z = new uint[64];
        for (int X = 0; X < 16; X++)
        {
            int y = E + 4 * X;
            z[X] = ((uint)q[y] << 24) | ((uint)q[y + 1] << 16) | ((uint)q[y + 2] << 8) | q[y + 3];
        }
        for (int w = 16; w < 64; w++)
        {
            uint U = z[w - 15];
            uint P = RotateRight(U, 7) ^ RotateRight(U, 18) ^ (U >> 3);
            uint J = z[w - 2];
            uint u = RotateRight(J, 17) ^ RotateRight(J, 19) ^ (J >> 10);
            z[w] = z[w - 16] + P + z[w - 7] + u;
        }

        uint o = W[0], R = W[1], F = W[2], A = W[3], V = W[4], p = W[5], C = W[6], M = W[7];
        for (int B = 0; B < 64; B++)
        {
            uint T = M + (RotateRight(V, 6) ^ RotateRight(V, 11) ^ RotateRight(V, 25)) + ((V & p) ^ (~V & C)) + Hj[B] + z[B];
            uint r = (RotateRight(o, 2) ^ RotateRight(o, 13) ^ RotateRight(o, 22)) + ((o & R) ^ (o & F) ^ (R & F));
            M = C; C = p; p = V; V = A + T; A = F; F = R; R = o; o = T + r;
        }
        W[0] += o; W[1] += R; W[2] += F; W[3] += A; W[4] += V; W[5] += p; W[6] += C; W[7] += M;
    }

    private static byte[] Kj(uint[] W, byte[] n, int q)
    {
        uint[] E = (uint[])W.Clone();
        int z = q + n.Length;
        int o = n.Length + 1;
        while (o % 64 != 56) o++;
        byte[] R = new byte[o + 8];
        Buffer.BlockCopy(n, 0, R, 0, n.Length);
        R[n.Length] = 128;
        long F = 8L * z;
        uint A = (uint)(F / 4294967296L);
        uint V = (uint)F;
        int p = R.Length - 8;
        R[p] = (byte)((A >> 24) & 255);
        R[p + 1] = (byte)((A >> 16) & 255);
        R[p + 2] = (byte)((A >> 8) & 255);
        R[p + 3] = (byte)(255 & A);
        R[p + 4] = (byte)((V >> 24) & 255);
        R[p + 5] = (byte)((V >> 16) & 255);
        R[p + 6] = (byte)((V >> 8) & 255);
        R[p + 7] = (byte)(255 & V);

        for (int M = 0; M < R.Length; M += 64)
            Jj(E, R, M);

        byte[] result = new byte[32];
        for (int X = 0; X < 8; X++)
        {
            result[4 * X] = (byte)((E[X] >> 24) & 255);
            result[4 * X + 1] = (byte)((E[X] >> 16) & 255);
            result[4 * X + 2] = (byte)((E[X] >> 8) & 255);
            result[4 * X + 3] = (byte)(255 & E[X]);
        }
        return result;
    }

    private static uint[] Lj(byte[] W, int n)
    {
        uint[] q = new uint[8];
        for (int E = 0; E < 8; E++)
        {
            int z = n + 4 * E;
            q[E] = ((uint)W[z] << 24) | ((uint)W[z + 1] << 16) | ((uint)W[z + 2] << 8) | W[z + 3];
        }
        return q;
    }

    private static string Wj(byte[] W, string n)
    {
        byte[] q = Encoding.UTF8.GetBytes(n);
        byte[] E = Kj(Lj(W, 0), q, 64);
        byte[] finalBytes = Kj(Lj(W, 32), E, 64);

        var sb = new StringBuilder();
        for (int i = 0; i < finalBytes.Length; i += 3)
        {
            int b0 = finalBytes[i];
            int b1 = i + 1 < finalBytes.Length ? finalBytes[i + 1] : 0;
            int b2 = i + 2 < finalBytes.Length ? finalBytes[i + 2] : 0;
            sb.Append(Nj[b0 >> 2]);
            sb.Append(Nj[((3 & b0) << 4) | (b1 >> 4)]);
            if (i + 1 < finalBytes.Length) sb.Append(Nj[((15 & b1) << 2) | (b2 >> 6)]);
            if (i + 2 < finalBytes.Length) sb.Append(Nj[63 & b2]);
        }
        string full = sb.ToString();
        return full.Length >= 16 ? full[..16] : full;
    }

    public static string GenerateToken(string pathAndQuery)
    {
        return "yti." + Wj(KeyTable, pathAndQuery);
    }

    public static string DecryptPayload(string base64Payload)
    {
        if (string.IsNullOrWhiteSpace(base64Payload)) return string.Empty;

        string s = base64Payload.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        byte[] decoded = Convert.FromBase64String(s);

        // Trong thuật toán apicore.js: z có 2 byte đầu = 0, sau đó là mảng decoded!
        int qLen = decoded.Length + 2;
        int bufLen = (qLen + 3) & ~3; // làm tròn lên bội số của 4
        byte[] z = new byte[bufLen];
        Buffer.BlockCopy(decoded, 0, z, 2, decoded.Length);

        int E = ((z[2] << 8) | z[3]) % 1024;
        byte[] o = new byte[1024];
        Buffer.BlockCopy(KeyTable, 64 + E, o, 0, 1024 - E);
        Buffer.BlockCopy(KeyTable, 64, o, 1024 - E, E);

        int A = qLen >> 2;
        uint[] R = new uint[z.Length / 4];
        Buffer.BlockCopy(z, 0, R, 0, z.Length);
        uint[] F = new uint[256];
        Buffer.BlockCopy(o, 0, F, 0, 1024);

        for (int p = 1; p < A; p++)
        {
            R[p] ^= F[(p - 1) % 256];
        }
        Buffer.BlockCopy(R, 0, z, 0, z.Length);

        for (int M = A << 2; M < qLen; M++)
        {
            z[M] ^= o[(M - 4) % 1024];
        }

        return Encoding.UTF8.GetString(z, 4, qLen - 4);
    }
}

