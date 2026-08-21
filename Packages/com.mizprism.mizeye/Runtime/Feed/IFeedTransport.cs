// フィードの取得口。実装 (UnityWebRequest 等) はエディタ側に置く。
//
// なぜインターフェースなのか: Runtime asmdef は noEngineReferences: true なので、ここでネットワーク API を直に呼べない。加えて、
// データ層の振る舞い (到達不可・改竄・レート制限) を**ネットワーク無しで実測できる**ことが
// テストの前提になる。
//
// **失敗で例外を投げない**。縮退系 (フィード到達不可 → キャッシュ表示 +
// 「最終取得: N 日前」) は異常系ではなく**正常系**であり、正常系を例外で表すと、
// 呼び出し側の「うっかり catch し忘れ」が利用者に見える壊れ方になる。

namespace Mizprism.LicenseLens
{
    /// <summary>1 回の取得の結果。到達できなかったことは Reachable=false で表す (例外ではない)。</summary>
    public struct FeedTransportResult
    {
        private readonly bool _reachable;
        private readonly byte[] _body;
        private readonly string _error;

        private FeedTransportResult(bool reachable, byte[] body, string error)
        {
            _reachable = reachable;
            _body = body;
            _error = error;
        }

        /// <summary>取得できたか。false なら Body は見ない。</summary>
        public bool Reachable => _reachable;

        /// <summary>取得したバイト列。**バイト列のまま渡す** (文字列に直すと改行変換や BOM が混ざる)。</summary>
        public byte[] Body => _body;

        /// <summary>到達できなかった理由 (利用者に見せる文言の材料)。</summary>
        public string Error => _error;

        public static FeedTransportResult Ok(byte[] body) =>
            new FeedTransportResult(true, body, null);

        public static FeedTransportResult Unreachable(string error) =>
            new FeedTransportResult(false, null, error);
    }

    public interface IFeedTransport
    {
        /// <summary>フィード基準の相対パス ("index.json" / "index.json.sig" / "items/00.json") を取得する。</summary>
        FeedTransportResult Get(string relativePath);
    }
}
