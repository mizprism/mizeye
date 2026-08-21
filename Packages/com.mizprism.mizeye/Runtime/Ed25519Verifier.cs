// SPDX-License-Identifier: Apache-2.0
// 相互運用のための狭い例外 (LICENSING.md)。リポジトリの既定は AGPL-3.0-only。
// Ed25519 signature verification (RFC 8032), pure managed.
//
// なぜ自前実装なのか: .NET / Unity の System.Security.Cryptography に Ed25519 が無い。
// Python と C# で「同一実装」は文字どおりには達成
// できないので、達成するのは (a) 同一の検証対象定義 (index.json のファイルバイト列に対する
// Ed25519 署名) と (b) 共通の conformance fixture である。
//
// **検証専用**。鍵生成も署名もここには無い (秘密鍵を扱うコードをリポジトリに置かない —
// 収集側の物理分離と同根)。検証は秘密も乱数も扱わないので、
// 「暗号を手書きする」危険の中核 (nonce 再利用・鍵リーク) が構造的に無い。
//
// 対応する Python 実装: tools/ed25519_verify.py

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Mizprism.LicenseLens
{
    public static class Ed25519Verifier
    {
        // p = 2^255 - 19
        private static readonly BigInteger P =
            BigInteger.Pow(2, 255) - 19;

        // L = 2^252 + 27742317777372353535851937790883648493 (群の位数)
        private static readonly BigInteger L =
            BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");

        // d = -121665 / 121666 mod p
        private static readonly BigInteger D =
            Mod(new BigInteger(-121665) * Inv(new BigInteger(121666)));

        // sqrt(-1) mod p
        private static readonly BigInteger SqrtM1 =
            BigInteger.ModPow(2, (P - 1) / 4, P);

        /// <summary>署名が公開鍵とメッセージに対して正しいかを返す。例外を投げず、疑わしければ false。</summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || message == null || signature == null) return false;
            if (publicKey.Length != 32 || signature.Length != 64) return false;

            var rBytes = new byte[32];
            var sBytes = new byte[32];
            Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
            Buffer.BlockCopy(signature, 32, sBytes, 0, 32);

            // S は L 未満でなければならない (可鍛性のある署名を通さない)
            BigInteger s = LittleEndianToBigInteger(sBytes);
            if (s >= L) return false;

            Point a, r;
            if (!TryDecompress(publicKey, out a)) return false;
            if (!TryDecompress(rBytes, out r)) return false;

            byte[] toHash = new byte[64 + message.Length];
            Buffer.BlockCopy(rBytes, 0, toHash, 0, 32);
            Buffer.BlockCopy(publicKey, 0, toHash, 32, 32);
            Buffer.BlockCopy(message, 0, toHash, 64, message.Length);

            byte[] digest;
            using (var sha512 = SHA512.Create())
            {
                digest = sha512.ComputeHash(toHash);
            }
            BigInteger k = Mod(LittleEndianToBigInteger(digest), L);

            // [S]B == R + [k]A を点の圧縮表現で比較する (射影座標のままの等価判定を避ける)
            Point lhs = ScalarMultiply(BasePoint(), s);
            Point rhs = Add(r, ScalarMultiply(a, k));
            return ConstantTimeEquals(Compress(lhs), Compress(rhs));
        }

        // ---- 有限体 ----

        private static BigInteger Mod(BigInteger x) => Mod(x, P);

        private static BigInteger Mod(BigInteger x, BigInteger m)
        {
            BigInteger r = x % m;
            return r.Sign < 0 ? r + m : r;
        }

        private static BigInteger Inv(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

        private static BigInteger LittleEndianToBigInteger(byte[] bytes)
        {
            var buf = new byte[bytes.Length + 1]; // 最上位に 0 を足して常に非負として読む
            Buffer.BlockCopy(bytes, 0, buf, 0, bytes.Length);
            return new BigInteger(buf);
        }

        // ---- 点 (拡張ツイステッド・エドワーズ座標 X:Y:Z:T, a = -1) ----

        private struct Point
        {
            public BigInteger X, Y, Z, T;
        }

        private static Point BasePoint()
        {
            BigInteger y = Mod(new BigInteger(4) * Inv(new BigInteger(5)));
            BigInteger x = RecoverX(y, 0);
            return new Point { X = x, Y = y, Z = BigInteger.One, T = Mod(x * y) };
        }

        private static BigInteger RecoverX(BigInteger y, int sign)
        {
            BigInteger y2 = Mod(y * y);
            BigInteger u = Mod(y2 - 1);
            BigInteger v = Mod(D * y2 + 1);
            BigInteger x = Mod(u * Inv(v));
            x = BigInteger.ModPow(x, (P + 3) / 8, P);
            if (Mod(x * x - Mod(u * Inv(v))) != BigInteger.Zero) x = Mod(x * SqrtM1);
            if ((int)Mod(x, 2) != sign) x = Mod(-x); // 255 bit を int にキャストしない (最下位ビットだけが要る)
            return x;
        }

        private static bool TryDecompress(byte[] encoded, out Point point)
        {
            point = default(Point);
            var yBytes = new byte[32];
            Buffer.BlockCopy(encoded, 0, yBytes, 0, 32);
            int sign = (yBytes[31] >> 7) & 1;
            yBytes[31] &= 0x7f;
            BigInteger y = LittleEndianToBigInteger(yBytes);
            if (y >= P) return false; // 非正規な符号化は受け付けない

            BigInteger y2 = Mod(y * y);
            BigInteger u = Mod(y2 - 1);
            BigInteger v = Mod(D * y2 + 1);
            if (v.IsZero) return false;
            BigInteger x = Mod(u * Inv(v));
            BigInteger candidate = BigInteger.ModPow(x, (P + 3) / 8, P);
            if (Mod(candidate * candidate) != x)
            {
                candidate = Mod(candidate * SqrtM1);
                if (Mod(candidate * candidate) != x) return false; // 平方根が無い = 曲線上にない
            }
            if (candidate.IsZero && sign == 1) return false;
            if ((int)Mod(candidate, 2) != sign) candidate = Mod(-candidate);

            point = new Point { X = candidate, Y = y, Z = BigInteger.One, T = Mod(candidate * y) };
            return true;
        }

        private static byte[] Compress(Point p)
        {
            BigInteger zInv = Inv(p.Z);
            BigInteger x = Mod(p.X * zInv);
            BigInteger y = Mod(p.Y * zInv);
            byte[] outBytes = new byte[32];
            byte[] yb = y.ToByteArray();
            Buffer.BlockCopy(yb, 0, outBytes, 0, Math.Min(32, yb.Length));
            outBytes[31] = (byte)(outBytes[31] | (byte)((int)Mod(x, 2) << 7));
            return outBytes;
        }

        private static Point Add(Point p, Point q)
        {
            BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
            BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
            BigInteger c = Mod(p.T * 2 * D * q.T);
            BigInteger d = Mod(p.Z * 2 * q.Z);
            BigInteger e = Mod(b - a);
            BigInteger f = Mod(d - c);
            BigInteger g = Mod(d + c);
            BigInteger h = Mod(b + a);
            return new Point { X = Mod(e * f), Y = Mod(g * h), Z = Mod(f * g), T = Mod(e * h) };
        }

        private static Point ScalarMultiply(Point p, BigInteger scalar)
        {
            var result = new Point
            {
                X = BigInteger.Zero, Y = BigInteger.One, Z = BigInteger.One, T = BigInteger.Zero
            }; // 単位元
            Point addend = p;
            BigInteger k = scalar;
            while (k > BigInteger.Zero)
            {
                if (!k.IsEven) result = Add(result, addend);
                addend = Add(addend, addend);
                k >>= 1;
            }
            return result;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
