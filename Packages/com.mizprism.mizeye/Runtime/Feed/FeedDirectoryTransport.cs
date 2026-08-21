// ローカルディレクトリを配信元にする取得口。
//
// ## なぜ出荷物に入れるのか (テスト専用の置き場ではない)
//
// 1. **表示層を実データで動かせる唯一の手段** — 公開フィードのホスティングと署名鍵は
//    まだ無い。それまで `dist/feed` をそのまま読ませれば、一覧・詳細ペイン・
//    改訂通知・縮退表示を実物 (terms 51 件) で確認できる。fixture の模式データでは
//    「実データで初めて出る崩れ」が見えない。
// 2. **エンジン非依存なので CI が触れる** — UnityWebRequest 実装は Editor アセンブリ行きで
//    CI がコンパイルすらしないが、こちらは Runtime に置けて windows/ubuntu 両方で
//    テストされる。取得口の振る舞い (存在しないパス・不正なパス) を検証できる側に持つ。
// 3. オフライン環境・社内ミラーからの読み込みという実用も兼ねる。
//
// ## パストラバーサル
//
// 相対パスはフィード由来 (= 外から来る文字列) で、そのまま**ローカルのファイルパスになる**。
// ネットワーク実装なら 404 で済むが、こちらはディレクトリの外に出られる。
// 判定は **FeedPath.IsValidChunkPath に委ねる** — 同じ不変条件を 2 つの実装で持つと、
// 片方の修正が他方に届かない。
// FeedPath が扱わないのは index の 2 本だけなので、そこだけ literal で足す。
//
// ## 失敗の表し方
//
// **例外を投げない** (IFeedTransport の契約)。読めないことは Unreachable で返す。
// 縮退系は異常系ではなく正常系であり、例外で表すと呼び出し側の catch 漏れが
// 利用者に見える壊れ方になる。

using System;
using System.IO;

namespace Mizprism.LicenseLens
{
    public sealed class FeedDirectoryTransport : IFeedTransport
    {
        private readonly string _root;

        /// <param name="root">index.json を直下に持つディレクトリ (例: リポジトリの dist/feed)。</param>
        public FeedDirectoryTransport(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentNullException(nameof(root));
            _root = root;
        }

        /// <summary>配信元のディレクトリ (画面に「どこを読んでいるか」を出すため)。</summary>
        public string Root => _root;

        public FeedTransportResult Get(string relativePath)
        {
            if (!FeedSource.IsServablePath(relativePath))
                return FeedTransportResult.Unreachable("受け付けない相対パス: " + (relativePath ?? "(null)"));

            string path = _root;
            string[] parts = relativePath.Split('/');
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);

            try
            {
                if (!File.Exists(path)) return FeedTransportResult.Unreachable("404: " + relativePath);
                // バイト列のまま返す。テキストとして読むと改行変換や BOM が混ざり、
                // 署名対象が壊れる (FeedStructureGuardTests がこの規律を見張っている)。
                return FeedTransportResult.Ok(File.ReadAllBytes(path));
            }
            catch (IOException e)
            {
                return FeedTransportResult.Unreachable("読み取り失敗: " + relativePath + " — " + e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                return FeedTransportResult.Unreachable("読み取り権限なし: " + relativePath + " — " + e.Message);
            }
        }

        // 配信してよい相対パスの判定は FeedSource.IsServablePath に移した (2026-08-20)。
        // HTTP 取得口が増え、同じ allowlist を 2 実装で持つと片方の修正が他方に届かないため。
    }
}
