// チャンクの相対パスの検査。
//
// **ここがパストラバーサルの関門**。チャンクのパスはフィード由来 (= 外から来る文字列) で
// ありながら、そのままローカルキャッシュのファイルパスになる。`../` や絶対パス、
// ドライブレター、バックスラッシュを 1 つでも通すと、キャッシュの書き込みがキャッシュ
// ディレクトリの外に出る。Windows では区切り文字が 2 種類あるぶん穴が広い。
//
// 方針は allowlist のみ: 正規表現でいえば `^(items|terms)/[0-9a-f]{2,64}\.json$` に**完全一致**
// するものだけを通す。「危ないものを弾く」(denylist) ではなく「安全と判った形だけを通す」—
// 危険な表記は環境ごとに増えるが、許す形は仕様で決まっている (フィードのパス規則
// `items/{shard:02x}.json` と `terms/{terms_id の先頭 8 桁}.json`)。
//
// 正規表現を使わず手で書いているのは、この判定が何を通すのかをコードの見た目どおりにする
// ため (「.NET の正規表現の . が何に一致するか」を読み手に思い出させない)。

using System;

namespace Mizprism.LicenseLens
{
    public static class FeedPath
    {
        private const string ItemsPrefix = "items/";
        private const string TermsPrefix = "terms/";
        private const string JsonSuffix = ".json";

        /// <summary>フィードのチャンクとして受け入れてよい相対パスか。</summary>
        public static bool IsValidChunkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string stem;
            if (!TryStem(path, out stem)) return false;

            // schema v1.3: terms/ にのみ visual チャンク ("terms/visual-<slug>.json") を許す。
            // 文字集合は [a-z0-9-] に閉じるので '.'/'/'/'\' は入り得ない (traversal は構造的に不可)。
            if (path.StartsWith(TermsPrefix, StringComparison.Ordinal) &&
                stem.StartsWith(VisualStemPrefix, StringComparison.Ordinal))
                return IsValidVisualStem(stem);

            // 16 進小文字のみ。長さは shard 2 桁から terms_id 全長までを想定して 2〜64。
            if (stem.Length < 2 || stem.Length > 64) return false;
            for (int i = 0; i < stem.Length; i++)
            {
                char c = stem[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex) return false; // 大文字も弾く: "items/00.JSON" と "items/00.json" を同じ物にしない
            }
            return true;
        }

        private const string VisualStemPrefix = "visual-";

        // "visual-" + slug。slug は schema v1.3 の visualId (^visual:[a-z0-9][a-z0-9-]{2,63}$) と同じ:
        // 先頭は英数字、以降は英数字と '-' のみ、3〜64 文字。allowlist を広げているのではなく
        // 仕様で決まった第 2 の形を足している (フィードの terms パス規則と対)。
        private static bool IsValidVisualStem(string stem)
        {
            int slugStart = VisualStemPrefix.Length;
            int slugLength = stem.Length - slugStart;
            if (slugLength < 3 || slugLength > 64) return false;
            char first = stem[slugStart];
            if (!((first >= '0' && first <= '9') || (first >= 'a' && first <= 'z'))) return false;
            for (int i = slugStart; i < stem.Length; i++)
            {
                char c = stem[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>terms チャンクなら terms_id の先頭部分 (ファイル名の幹) を返す。それ以外は null。</summary>
        public static string TermsIdFromChunkPath(string path)
        {
            if (!IsValidChunkPath(path)) return null;
            if (!path.StartsWith(TermsPrefix, StringComparison.Ordinal)) return null;
            return path.Substring(TermsPrefix.Length, path.Length - TermsPrefix.Length - JsonSuffix.Length);
        }

        /// <summary>terms_ref ("sha256:…" / "visual:…") から terms チャンクの相対パスを作る。作れなければ null。</summary>
        public static string TermsChunkPath(string termsRef)
        {
            // フィードの terms パス規則: sha256 は先頭 8 桁で切り、visual (v1.3) は ':' を '-' に
            // 置換した全体を使う (8 桁に切ると slug が壊れて衝突しうる)。
            // ここを別の規則で作ると、正しいフィードなのに「チャンクが無い」になる。
            if (string.IsNullOrEmpty(termsRef)) return null;
            string candidate;
            if (termsRef.StartsWith("visual:", StringComparison.Ordinal))
            {
                candidate = TermsPrefix + VisualStemPrefix +
                            termsRef.Substring("visual:".Length) + JsonSuffix;
            }
            else
            {
                int colon = termsRef.IndexOf(':');
                if (colon < 0 || colon + 1 >= termsRef.Length) return null;
                string hex = termsRef.Substring(colon + 1);
                if (hex.Length < 8) return null;
                candidate = TermsPrefix + hex.Substring(0, 8) + JsonSuffix;
            }
            return IsValidChunkPath(candidate) ? candidate : null;
        }

        /// <summary>item id から items チャンクの相対パスを作る。作れなければ null。</summary>
        public static string ItemChunkPath(long boothItemId, int itemShards)
        {
            if (boothItemId < 0 || itemShards <= 0) return null;
            long shard = boothItemId % itemShards;
            // フィードが shard 名を書く時と同じ (最小 2 桁のゼロ埋め小文字 16 進)。
            string candidate = ItemsPrefix + shard.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + JsonSuffix;
            return IsValidChunkPath(candidate) ? candidate : null;
        }

        private static bool TryStem(string path, out string stem)
        {
            stem = null;
            bool items = path.StartsWith(ItemsPrefix, StringComparison.Ordinal);
            bool terms = path.StartsWith(TermsPrefix, StringComparison.Ordinal);
            if (!items && !terms) return false; // 先頭スラッシュ・ドライブレター・"../" はここで落ちる
            if (!path.EndsWith(JsonSuffix, StringComparison.Ordinal)) return false;

            int prefixLength = ItemsPrefix.Length; // items/ と terms/ は同じ長さ
            int stemLength = path.Length - prefixLength - JsonSuffix.Length;
            if (stemLength <= 0) return false;
            stem = path.Substring(prefixLength, stemLength);
            return true;
        }
    }
}
