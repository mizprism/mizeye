// 利用者に見せる文字列に、外から来た文字列を混ぜる時の消毒。
//
// **未検証データ由来の文字列を、そのままメッセージやログに埋めない**。FeedVerifier は
// 署名を確かめる前の index から signing_key_id を読む (順序 (1)) ので、その値は攻撃者が
// 自由に決められる。JSON のエスケープ (\n \r  …) は解いた後に本物の制御文字になるため、
// 素通しすると「ログの行を偽装する」「エディタのコンソール表示を壊す」ことができる。
//
// 検証を通ったフィード本文 (item 名など) はここを通していない。あれは MIZPRISM 自身が
// 署名した内容であり、表示のエスケープは表示層の責務 (データ層で内容を書き換えると、
// 原文と食い違うものを見せることになる)。

using System.Text;

namespace Mizprism.LicenseLens
{
    internal static class FeedText
    {
        /// <summary>メッセージに埋める既定の上限長。理由文が本文で埋まらない程度に短く。</summary>
        internal const int DefaultMaxLength = 120;

        /// <summary>制御文字を '?' に落とし、長すぎる入力を切り詰める。null はそのまま返す。</summary>
        internal static string Sanitize(string value)
        {
            return Sanitize(value, DefaultMaxLength);
        }

        internal static string Sanitize(string value, int maxLength)
        {
            if (value == null) return null;

            bool truncated = value.Length > maxLength;
            int length = truncated ? maxLength : value.Length;

            var sb = new StringBuilder(length + 1);
            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                // 制御文字 (C0 / DEL / C1) を落とす。改行もここで消える —
                // ログの 1 行を偽造されないことの方が、原文の見た目より重要。
                sb.Append(char.IsControl(c) ? '?' : c);
            }
            if (truncated) sb.Append('…');
            return sb.ToString();
        }
    }
}
