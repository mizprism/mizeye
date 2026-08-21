// 公開フィードを HTTP(S) で取る取得口。**Editor アセンブリにしか置けない**
// (Runtime asmdef は noEngineReferences: true なので UnityWebRequest を参照できない)。
//
// ## ここは薄い殻である
//
// 判断 (取りに行ってよいパスか / URL をどう組むか) は Runtime の FeedSource が持ち、
// CI の dotnet テストで検証される。このクラスに残るのは「実際に叩いて結果を写す」だけ。
// **Editor アセンブリは dotnet CI がコンパイルすらしない**ので、判断をここに書くと
// 検証されないコードになる。
//
// ## 同期で待つ
//
// IFeedTransport.Get は同期契約 (FeedClient も同期)。UnityWebRequest を投げて完了まで
// ポーリングで待つ = エディタスレッドを塞ぐ。エディタ拡張の取得は 1 日 1 回 + 手動なので
// 許容し、代わりに **timeout を必ず設定する** (無設定だと止まったホスト相手に固まる)。
// 非同期化するなら FeedClient ごと変える話になり、それは v0.1 の範囲ではない。
//
// ## 失敗の表し方
//
// **例外を投げない** (IFeedTransport の契約)。到達できないことは Unreachable で返す。
// 縮退系 (到達不可 → キャッシュ + 「最終取得: N 日前」) は異常系ではなく正常系。
// HTTP 200 以外は本文があっても Unreachable にする — エラーページのバイト列を
// 規約データとして下流に流さないため (収集側で踏んだのと同じ型の事故を作らない)。

using System;
using UnityEngine.Networking;

namespace Mizprism.LicenseLens.Editor
{
    /// <summary>公開フィードを HTTP(S) で取得する <see cref="IFeedTransport"/>。</summary>
    public sealed class FeedHttpTransport : IFeedTransport
    {
        /// <summary>1 リクエストの上限秒数。止まったホストでエディタを固めないための保険。</summary>
        public const int TimeoutSeconds = 30;

        private readonly string _root;

        /// <param name="root">index.json を直下に持つ URL (例: https://feed.mizprism.workers.dev)。</param>
        /// <exception cref="ArgumentException">URL として受け付けられない形の場合。</exception>
        public FeedHttpTransport(string root)
        {
            string normalized;
            string error;
            if (!FeedSource.TryNormalizeRoot(root, out normalized, out error))
                throw new ArgumentException(error, nameof(root));
            _root = normalized;
        }

        /// <summary>配信元 URL (画面に「どこを読んでいるか」を出すため)。</summary>
        public string Root => _root;

        public FeedTransportResult Get(string relativePath)
        {
            string url;
            string error;
            if (!FeedSource.TryComposeUrl(_root, relativePath, out url, out error))
                return FeedTransportResult.Unreachable(error);

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                try
                {
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        // ポーリングで待つ。エディタの同期文脈なので Sleep は入れない
                        // (入れると短いリクエストまで一律に遅くなる)。
                    }
                }
                catch (Exception e)
                {
                    // SendWebRequest が投げるのは想定外だが、契約は「例外を投げない」なので
                    // ここで受けて Unreachable に写す。
                    return FeedTransportResult.Unreachable("取得に失敗しました: " + url + " — " + e.Message);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    return FeedTransportResult.Unreachable(
                        "取得に失敗しました: " + url + " — " + request.error);

                // 200 以外は本文を捨てる。エラーページを規約データとして下流に流さない。
                if (request.responseCode != 200)
                    return FeedTransportResult.Unreachable(
                        "HTTP " + request.responseCode + ": " + url);

                byte[] body = request.downloadHandler != null ? request.downloadHandler.data : null;
                if (body == null)
                    return FeedTransportResult.Unreachable("本文が空でした: " + url);

                // バイト列のまま返す。文字列に直すと改行変換や BOM が混ざり署名対象が壊れる。
                return FeedTransportResult.Ok(body);
            }
        }
    }
}
