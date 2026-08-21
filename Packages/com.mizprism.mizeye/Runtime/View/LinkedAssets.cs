// 手動リンク済み商品 ID の並び。
//
// **保存先はプロジェクトの外** (Editor 側で EditorPrefs に置く)。// 「プロジェクトを一切変更しない」は本製品の前提であって、Assets/ に
// ScriptableObject を書いた時点で守れなくなる — ここが文字列 1 本で済む形に
// なっているのは、そのため。
//
// 直列化と解析だけをここに置く (Editor 側は EditorPrefs の読み書きしかしない)。
// 分けるのは、この解析が**壊れると利用者のリンクが黙って消える**タイプの処理で、
// EditorWindow の中では CI から見えないため。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mizprism.LicenseLens
{
    public static class LinkedAssets
    {
        private const char Separator = ',';

        /// <summary>
        /// 保存文字列 → 商品 ID の並び。**読めない要素は黙って捨てる**が、
        /// 読めたものは順序を保って残す — 1 つの壊れた要素で一覧全体を失わせない。
        /// </summary>
        public static IReadOnlyList<long> Parse(string stored)
        {
            var ids = new List<long>();
            if (string.IsNullOrEmpty(stored)) return ids;

            string[] parts = stored.Split(Separator);
            var seen = new HashSet<long>();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0) continue;

                long id;
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                if (id <= 0) continue;      // BOOTH の商品 ID は正の整数
                if (!seen.Add(id)) continue; // 重複は畳む (一覧側の畳み込みと同じ規律)
                ids.Add(id);
            }
            return ids;
        }

        public static string Format(IEnumerable<long> ids)
        {
            if (ids == null) return string.Empty;
            var parts = new List<string>();
            var seen = new HashSet<long>();
            foreach (long id in ids)
            {
                if (id <= 0 || !seen.Add(id)) continue;
                parts.Add(id.ToString(CultureInfo.InvariantCulture));
            }
            return string.Join(Separator.ToString(), parts.ToArray());
        }

        /// <summary>
        /// 利用者が貼り付けた文字列から商品 ID を取り出す。
        /// BOOTH の URL (https://booth.pm/ja/items/12345 / ?variant= 付き / shop サブドメイン形)
        /// と、ID を直接打った場合の両方を受ける。
        ///
        /// **数字なら何でも通す、はしない** — URL の中の別の数字 (ja/ の言語コード等) を
        /// 商品 ID として拾うと、利用者は無関係な規約を自分の資産のものとして読む。
        /// items/ の直後の数字だけを商品 ID と見なす。
        /// </summary>
        public static bool TryParseItemReference(string input, out long boothItemId)
        {
            boothItemId = 0;
            if (string.IsNullOrEmpty(input)) return false;
            string text = input.Trim();
            if (text.Length == 0) return false;

            const string Marker = "items/";
            int marker = text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                int start = marker + Marker.Length;
                int end = start;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
                if (end == start) return false;
                return TryParsePositive(text.Substring(start, end - start), out boothItemId);
            }

            // URL でないなら、全体が ID そのものである時だけ受ける。
            return TryParsePositive(text, out boothItemId);
        }

        private static bool TryParsePositive(string text, out long value)
        {
            value = 0;
            long parsed;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed <= 0) return false;
            value = parsed;
            return true;
        }
    }
}
