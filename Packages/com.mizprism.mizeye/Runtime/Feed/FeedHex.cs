// 16 進の相互変換 (鍵リングの公開鍵と、チャンクの content hash で使う)。
//
// 独立したファイルにしているのは、encode 側 (hash を "sha256:<hex>" にする) と decode 側
// (公開鍵 hex を 32 bytes に戻す) が同じ表記規約を共有しなければならないため。表記が割れると
// 「一致しているのに一致しないと判定する」類の沈黙した不整合になる。
//
// 表記規約: 出力は**小文字**。Python 側 (hashlib.hexdigest / ed25519_verify.key_id) が小文字を
// 出すので、比較は序数一致で足りる。

using System;
using System.Text;

namespace Mizprism.LicenseLens
{
    internal static class FeedHex
    {
        /// <summary>小文字 16 進にする。</summary>
        internal static string Encode(byte[] bytes)
        {
            if (bytes == null) return null;
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(HexDigit(bytes[i] >> 4));
                sb.Append(HexDigit(bytes[i] & 0x0f));
            }
            return sb.ToString();
        }

        /// <summary>16 進文字列をバイト列に戻す。桁数が奇数、16 進以外の文字があれば false。</summary>
        internal static bool TryDecode(string hex, out byte[] bytes)
        {
            bytes = null;
            if (hex == null || hex.Length == 0 || (hex.Length % 2) != 0) return false;

            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = Value(hex[i * 2]);
                int lo = Value(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                result[i] = (byte)((hi << 4) | lo);
            }
            bytes = result;
            return true;
        }

        /// <summary>すべて小文字 16 進で、長さが期待どおりか。</summary>
        internal static bool IsLowerHex(string s, int length)
        {
            if (s == null || s.Length != length) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        private static char HexDigit(int v) => (char)(v < 10 ? ('0' + v) : ('a' + (v - 10)));

        private static int Value(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10; // 入力は大文字も受ける (Python の bytes.fromhex と同じ寛容さ)
            return -1;
        }
    }
}
