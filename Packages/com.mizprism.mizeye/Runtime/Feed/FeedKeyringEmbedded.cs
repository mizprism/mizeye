// **生成物。手で編集しない。**
//
//   正本   = 鍵リング原本 (配布物には含まれない)
//   再生成 = 生成側のスクリプト
//
// 信頼の起点はクライアント同梱の keyring であって、フィード自身の自己申告ではない。
// したがってこの鍵はパッケージに入っている必要がある — リポジトリ側の
// 鍵リング原本は配布物に含まれない。
//
// **base64 で持つ理由**: 中身は署名検証の根拠なので、改行変換の効く形で運ばない。
// 2026-08-13、.gitattributes の穴で Windows の checkout が LF→CRLF 変換を掛け、署名対象の
// バイト列が変わって windows-latest でだけテストが落ちた (Linux は緑のまま)。base64 の
// 1 行はその変換を受けても中身が変わらない。**正本のバイト列そのもの**を運んでいるので、
// 再シリアライズによる差も入らない。
//
// 原本を編集して再生成し忘れると、**原本を持つ側の適合スイート**が赤くなる
// (デコード結果と原本のバイト列を 1 バイト単位で突き合わせている)。この検出器は原本と
// 一緒にしか置けない — 生成物の隣にコピーを置いて突き合わせても、同じ生成器が両方を
// 書くので自己一致になり、検証でなくなる。

using System;

namespace Mizprism.LicenseLens
{
    /// <summary>パッケージに同梱された公開鍵リング。</summary>
    public static class FeedKeyringEmbedded
    {
        /// <summary>鍵リング原本のバイト列そのもの (base64)。</summary>
        private const string CanonicalBase64 =
            "ewogIiRjb21tZW50IjogWwogICLjg5XjgqPjg7zjg4nnvbLlkI3jga7lhazplovpjbXjg6rjg7PjgrDjgIIiLAogICIqKuOB" +
            "k+OBruODleOCoeOCpOODq+OBr+ODkOOCpOODiOWIl+OBruOBvuOBvuOCr+ODqeOCpOOCouODs+ODiOOBq+WQjOaiseOBleOC" +
            "jOOBpumFjeW4g+OBleOCjOOCi+OAgioqIiwKICAi6Kqt44KA44Gu44Gv5Yip55So6ICF44Gq44Gu44Gn44CB56eY5a+G6Y21" +
            "44O744Gd44Gu5L+d566h5aC05omA44O76Z2e5YWs6ZaL44Gu6YGL55So5omL6aCG44G444Gu5Y+C54Wn44KS5pu444GL44Gq" +
            "44GE44CCIiwKICAi44GT44GT44Gr44Gv5YWs6ZaL6Y2144GX44GL572u44GL44Gq44GE44CC56eY5a+G6Y2144Gv44GT44Gu" +
            "44Oq44Od44K444OI44Oq44Gr5YWl44KM44Gm44Gv44GE44GR44Gq44GE44CCIiwKICAic3RhdHVzOiBjdXJyZW50ID0g44GE" +
            "44G+572y5ZCN44Gr5L2/44GG6Y21IC8gbmV4dCA9IOODreODvOODhuODvOOCt+ODp+ODs+WFiOOBqOOBl+OBpuWFiOOBq+mF" +
            "jeOCi+mNtSAvIiwKICAicmV2b2tlZCA9IOWkseWKueOAguOCr+ODqeOCpOOCouODs+ODiOOBryByZXZva2VkIOOBrumNteOB" +
            "p+e9suWQjeOBleOCjOOBn+ODleOCo+ODvOODieOCkuWPl+OBkeWFpeOCjOOBquOBhOOAgiIsCiAgIioqbmV4dCDjgpLlhYjj" +
            "gavphY3jgaPjgabjgYvjgokgY3VycmVudCDjgpLliIfjgormm7/jgYjjgosqKiDigJQg5YWs6ZaL6Y2144Gv44Kv44Op44Kk" +
            "44Ki44Oz44OI5ZCM5qKx44Gq44Gu44Gn44CBIiwKICAi6Y2144KS5pu/44GI44Gm44GL44KJ6YWN44KL44Go44CB5pu05paw" +
            "5YmN44Gu44Kv44Op44Kk44Ki44Oz44OI44GM5YWo5ZOh5qSc6Ki85aSx5pWX44Gr44Gq44KL44CCIiwKICAia2V5cyDjgYzn" +
            "qbrjga7plpPjgIHjg5XjgqPjg7zjg4njga/mnKrnvbLlkI3jgafjgYLjgorphY3kv6HjgZfjgabjga/jgYTjgZHjgarjgYTj" +
            "gIIiCiBdLAogImtleXMiOiBbCiAgewogICAia2V5X2lkIjogInNoYTI1Njo2MzhhZThlMGZmOTQ1NzA2M2VhMTBmMDU0MzI0" +
            "MzM0OTUyY2JlNTc0OWQ2MzY0MGU5NDA5NjAwYmUxNzkyZDM5IiwKICAgInB1YmxpY19rZXlfaGV4IjogIjhhNTE1ZTYxY2Fl" +
            "YzMzZGI3ZjMwYzY4NWQzNTE4ZTBlMmVjMjI2YTIwNWFiZmNhN2I1OTZlMmVmZThjNGU0YzciLAogICAic3RhdHVzIjogImN1" +
            "cnJlbnQiLAogICAic2luY2UiOiAiMjAyNi0wOC0xMyIsCiAgICJub3RlIjogInByb2R1Y3Rpb24gZmVlZCBzaWduaW5nIGtl" +
            "eSAjMSIKICB9CiBdCn0K";

        /// <summary>
        /// 同梱の keyring を読む。**壊れていても例外を投げない** — 起点が読めないことは
        /// 「検証できない」であって、エディタの表示を落とす理由ではない。失敗は理由付きで
        /// 返して呼び出し側に判断させる。
        /// </summary>
        public static bool TryLoad(out FeedKeyring keyring, out string error)
        {
            return TryLoad(CanonicalBase64, out keyring, out error);
        }

        /// <summary>同梱バイト列の複製 (正本との突き合わせ用)。壊れていれば null。</summary>
        internal static byte[] TryDecode()
        {
            try
            {
                return Convert.FromBase64String(CanonicalBase64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>base64 を差し替えられる本体 (壊れた同梱物の挙動を試験できるようにするため)。</summary>
        internal static bool TryLoad(string base64, out FeedKeyring keyring, out string error)
        {
            keyring = null;
            error = null;

            byte[] utf8;
            try
            {
                utf8 = Convert.FromBase64String(base64);
            }
            catch (FormatException e)
            {
                error = "同梱 keyring の base64 を読めない (再生成が要る): " + e.Message;
                return false;
            }

            string parseError;
            if (!FeedKeyring.TryParse(utf8, out keyring, out parseError))
            {
                keyring = null;
                error = "同梱 keyring を読めない: " + parseError;
                return false;
            }
            return true;
        }
    }
}
