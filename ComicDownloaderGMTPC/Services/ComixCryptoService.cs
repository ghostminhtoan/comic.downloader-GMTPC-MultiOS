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
        "e2U1m5X3IX3lh22R/ZUZBKEW4gsD5qXBy7ns0ppgv51TXZzGtd98ciTr0NLJB1KwalZZve/skthhF9dmLFiqsAc6ut0e" +
        "onGGB+hyee0K4/+H4Y0PW8MgtSVKIiVCrXJ5PolPISHXIqWT6gRsUrPlrKfFX7xwq69996E9H4SGSv1xHupcV3d8W6OF" +
        "gITqmQf3Gx1qtrLRr3PyIF7QZ3DEsbI/8sB1SLkdA7avTvVoelDGES/W1j4qyq0jauW8wQAi5i/Kf1s0NsDVdOhY6EMQ" +
        "4nYQYxEHNulruaL2hiKrXwfbXXaaOWVoYXu4TpYuDo9p/y6ACIy1CWpD7XKRq9HMviPcjUcvbAwvfY1u2hqKs+AgbTI1" +
        "KMBTNy6hJxh/DU1HS3A9f+qdW02D/RvKaXIxfdXdwKvUjv4gkhMOCdZDuro7a+mT8OXsFp1hpV0TclZPqFQ1m8OsP8yF" +
        "Q3CSMveRSZU5sp63sEcexafzKj8bOQinz7/j7faHMXHDm5z6h6uUG/c1zK0iDum41Vwk8hUZDL+Fpz+DQ3tIsch3KCg7" +
        "KJwcMnW2jmbkJgtBwaIb5ELki7E3JbNBIQDOk24luE4TMr5RTDArlGgEexraMJbxpfdX4QjO8SjHEKORFdJ0gVd5C1Bh" +
        "RGgx8/qAKkqkVn8FiYn2WGexOc1P1iwskFaBOZ94ODb7pnCsNydD+a0wnYaGwYcjm2P32hyaIG4gAHKAgiaginvQ4UVk" +
        "k9xSaTkPzppqgQhVBQSt8lEtt7ezVj/ZYAHULBiSvF/GKxmR7AKy72NFnSEuJGQA/WfPG1ut4Ip81bJp7L8Y3sZnhjZJ" +
        "D8Z5A44Kzf8zqXVAwspd+1In2M1Spx8+2EhWwKzr28mX2ycS93oUhV2dirqmIaKcXSL09t6HCgZvoNWypxFfLGfyERUE" +
        "oBAarPzeL2o6+FsprT5TFgZJIubCm0a8y+yv+6+Jj8mqUjtiIE4MtBOUaqoBvkbSkSXYB+5Z+8bC025S3CXCBuqe+TUZ" +
        "7LMFeUmoKOktzNQ/QD9sdTSTLHxFNxIaXJyE41J1TpbUYVx3koC9hmtd7oFtkUiUU1HwJnGox/5/iXGWnAVDMWrqs8KJ" +
        "7WJqeqa1SdjgUSeSvEU7f1PF3QSgldwXSe36HG8mxdSg2EqHi0PwGucNREsG/SlnPsAJpNILoxU3keYMOdl11hypJ+R7" +
        "dwy+LDO0MnvGYW9xiBQ2CB99QfZH0IjqXRknnAfWe3NZRFx+gIgBD0rjBg7ZlEodAaIZJyuu4AXZ4oEesN4yFg/eHcr4" +
        "DMBDDvnvCeIZkHJ/SqrlvEnPmtZZktYLNtvtA0EohRh3Xz9TZCuWS4yh6izc/YisO8ais1EgGlEZPzwPdxZdevBThFZE" +
        "624vCBE2c6D4+/LL8dwGEjckIruN1zK4bxgIYyh/a2HDGm9XnM7MvbpYB0Fx7jYYMwoOEJM=";

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
        return "qey." + Wj(KeyTable, pathAndQuery);
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

