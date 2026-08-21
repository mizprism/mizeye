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
// ## 同期で待つ (ただしエディタのループを止めない待ち方で)
//
// IFeedTransport.Get は同期契約 (FeedClient も同期)。呼び出し中エディタスレッドが塞がるのは
// 変わらないので、**timeout を必ず設定する** (無設定だと止まったホスト相手に固まる)。
//
// **UnityWebRequest を `while (!isDone) {}` で待ってはいけない** (2026-08-21 実測)。
// 完了処理はエディタのループ上で進むため、そのループを 100% 占有して回す待ち方は自分で
// 自分を待たせる。同一コンテナ・同一ネットワークで比較した数字:
//
//   素の逐次取得 (90 チャンク) .......... 2.53 秒 (1 件あたり約 28ms)
//   UnityWebRequest + 空回し (92 件) .... 134.25 秒 (1 件あたり約 1,460ms)
//
// 53 倍で、しかも 1 件あたりがほぼ一定 — 遅いのはネットワークではなく待ち方だった。
// 旧コードのコメントは「Sleep を入れると短いリクエストまで一律に遅くなる」と書いていたが、
// 実際には Sleep なしの空回しの方が一律に遅くしていた。
//
// そこで HttpClient で取る。接続を再利用でき (同一ホストへ 92 連射する形なので効く)、
// 待っている間エディタのループを塞がない。非同期化 (取得中も UI が動く) は別の話で、
// それは FeedClient ごと変える範囲。
//
// ## 失敗の表し方
//
// **例外を投げない** (IFeedTransport の契約)。到達できないことは Unreachable で返す。
// 縮退系 (到達不可 → キャッシュ + 「最終取得: N 日前」) は異常系ではなく正常系。
// HTTP 200 以外は本文があっても Unreachable にする — エラーページのバイト列を
// 規約データとして下流に流さないため (収集側で踏んだのと同じ型の事故を作らない)。

using System;
using System.Net.Http;

namespace Mizprism.LicenseLens.Editor
{
    /// <summary>公開フィードを HTTP(S) で取得する <see cref="IFeedTransport"/>。</summary>
    public sealed class FeedHttpTransport : IFeedTransport
    {
        /// <summary>1 リクエストの上限秒数。止まったホストでエディタを固めないための保険。</summary>
        public const int TimeoutSeconds = 30;

        private readonly string _root;

        /// <summary>
        /// **使い回す** — 生成のたびに新しくすると接続も TLS ハンドシェイクも毎回やり直しになる。
        /// フィード 1 回の更新で同一ホストへ 90 件超を連射するので、ここが効く。
        /// </summary>
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            // 既定の UA は環境依存で、配信側の bot 判定に当たりうる (Cloudflare が Python の
            // 既定 UA を 403 で弾く実例を 2026-08-21 に実測)。自分が誰かを名乗る。
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MizEye/1.0 (+https://github.com/mizprism/mizeye)");
            return client;
        }

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

            try
            {
                using (HttpResponseMessage response = Client.GetAsync(url).GetAwaiter().GetResult())
                {
                    // 200 以外は本文を捨てる。エラーページを規約データとして下流に流さない。
                    if ((int)response.StatusCode != 200)
                        return FeedTransportResult.Unreachable(
                            "HTTP " + (int)response.StatusCode + ": " + url);

                    // バイト列のまま返す。文字列に直すと改行変換や BOM が混ざり署名対象が壊れる。
                    byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if (body == null || body.Length == 0)
                        return FeedTransportResult.Unreachable("本文が空でした: " + url);

                    return FeedTransportResult.Ok(body);
                }
            }
            catch (Exception e)
            {
                // 契約は「例外を投げない」。到達できないことは Unreachable で返す
                // (timeout もここに落ちる — TaskCanceledException)。
                Exception inner = e is AggregateException agg && agg.InnerException != null
                    ? agg.InnerException
                    : e;
                return FeedTransportResult.Unreachable(
                    "取得に失敗しました: " + url + " — " + inner.Message);
            }
        }
    }
}
