// 配信元 (source) の判定と URL 組み立て。**エンジン非依存**なので CI がテストできる。
//
// ## なぜ Runtime に置くのか
//
// HTTP 取得の実体 (UnityWebRequest) は Editor アセンブリにしか置けない (Runtime asmdef は
// noEngineReferences: true)。だが「どのパスを取りに行ってよいか」「URL をどう組むか」は
// ネットワークと無関係の純粋な判断で、**そこが壊れると Editor でしか気付けない**。
// 判断だけを engine 非依存の側に降ろし、Editor 側は薄い殻にする。
//
// ## 取得してよいパスの allowlist は 1 つだけ持つ
//
// `IsServablePath` は元々 FeedDirectoryTransport の private メソッドだった。HTTP 実装が
// 増えるとこの不変条件が 2 箇所に生まれ、片方の修正が他方に届かなくなる。
// 両方の取得口がここを呼ぶ。
//
// allowlist であって denylist ではない — "../" を弾くのではなく、安全と判った形だけ通す。
// ディレクトリ実装ではパストラバーサル、HTTP 実装では「フィード以外の URL を取りに行く」
// を同時に塞ぐ。

using System;

namespace Mizprism.LicenseLens
{
    /// <summary>配信元の種別判定と、取得してよいパスの判定。</summary>
    public static class FeedSource
    {
        /// <summary>index 本体の相対パス。</summary>
        public const string IndexPath = "index.json";

        /// <summary>index の署名 (サイドカー) の相対パス。</summary>
        public const string SignaturePath = "index.json.sig";

        private const string HttpsPrefix = "https://";
        private const string HttpPrefix = "http://";

        /// <summary>
        /// 取得してよい相対パスか。index の 2 本 + FeedPath が認めるチャンクのみ。
        /// **両方の取得口 (ディレクトリ / HTTP) がこれを使う。**
        /// </summary>
        public static bool IsServablePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            if (string.Equals(relativePath, IndexPath, StringComparison.Ordinal)) return true;
            if (string.Equals(relativePath, SignaturePath, StringComparison.Ordinal)) return true;
            return FeedPath.IsValidChunkPath(relativePath);
        }

        /// <summary>
        /// 配信元の文字列が HTTP(S) の URL に見えるか。見えなければローカルディレクトリとして扱う。
        /// 判定だけで、到達できるかは問わない。
        /// </summary>
        public static bool LooksLikeHttpUrl(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase)
                || source.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 配信元 URL として受け付けられる形か調べ、末尾の '/' を落とした正規形を返す。
        /// </summary>
        /// <remarks>
        /// クエリ・フラグメントを持つ URL を弾くのは、後ろに "/index.json" を繋ぐと
        /// **別物の URL になる**ため ("…?x=1" + "/index.json" は取りに行きたい場所ではない)。
        /// 静かに変な URL を叩くより、受け取った時点で断る。
        /// </remarks>
        public static bool TryNormalizeRoot(string root, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            if (string.IsNullOrEmpty(root))
            {
                error = "配信元が空です。";
                return false;
            }
            if (!LooksLikeHttpUrl(root))
            {
                error = "http:// または https:// で始まる URL を指定してください: " + root;
                return false;
            }
            if (root.IndexOf('?') >= 0 || root.IndexOf('#') >= 0)
            {
                error = "クエリ (?) やフラグメント (#) を含む URL は使えません: " + root;
                return false;
            }

            // ホスト名の検査は **末尾スラッシュを削る前の文字列**で行う。
            // "https://" を先に TrimEnd('/') すると "https:" になり、"//" が消えるので
            // スキーム位置を見失う (最初の実装はここで取りこぼし、FeedSourceTests が捕まえた)。
            int schemeSep = root.IndexOf("//", StringComparison.Ordinal);
            string rest = root.Substring(schemeSep + 2);
            int firstSlash = rest.IndexOf('/');
            string host = firstSlash >= 0 ? rest.Substring(0, firstSlash) : rest;
            if (host.Length == 0)
            {
                error = "ホスト名がありません: " + root;
                return false;
            }

            normalized = root.TrimEnd('/');
            return true;
        }

        /// <summary>
        /// 配信元 URL と相対パスから取得先 URL を作る。作れない場合は false を返し、
        /// 理由を <paramref name="error"/> に入れる (**例外は投げない** — IFeedTransport の契約と同じ)。
        /// </summary>
        public static bool TryComposeUrl(string root, string relativePath, out string url, out string error)
        {
            url = null;

            string normalizedRoot;
            if (!TryNormalizeRoot(root, out normalizedRoot, out error)) return false;

            if (!IsServablePath(relativePath))
            {
                error = "受け付けない相対パス: " + (relativePath ?? "(null)");
                return false;
            }

            url = normalizedRoot + "/" + relativePath;
            error = null;
            return true;
        }
    }
}
