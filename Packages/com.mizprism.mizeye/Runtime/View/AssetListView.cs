// 資産一覧 × 規約状態と、縮退系。
//
// 「未収載」と「引けない」を絶対に混ぜない。データ層は既にこれを FeedItemLookup で
// 分けている (NotListed / NotAvailable) が、**表示で混ぜたら分けた意味が消える**:
//
//   - 未収載 = フィードには載っていないと**言い切れる**。収載リクエストへ案内できる
//   - 引けない = キャッシュが無い等で、収載の有無すら**言えない**。案内してはいけない
//
// 前者を後者として出すと利用者は無駄に待ち、後者を前者として出すと「載っていない」という
// 事実でない断定を配ることになる。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    /// <summary>一覧 1 行の状態。**既定値 0 = Unavailable** (何も言えない側が安全)。</summary>
    public enum AssetRowState
    {
        /// <summary>キャッシュやチャンクが手元に無く、収載の有無を言えない。</summary>
        Unavailable = 0,
        /// <summary>フィードに収載されていない (収載リクエストへ案内してよい)。</summary>
        NotListed,
        /// <summary>収載されているが、参照先の規約チャンクが手元に無い。</summary>
        TermsUnavailable,
        /// <summary>収載されていて規約も読める。</summary>
        Listed
    }

    /// <summary>資産一覧の 1 行。</summary>
    public sealed class AssetRowView
    {
        internal AssetRowView(long boothItemId, AssetRowState state, FeedItem item,
                              FeedTerms terms, bool revised, string reason)
        {
            BoothItemId = boothItemId;
            State = state;
            Item = item;
            Terms = terms;
            Revised = revised;
            Reason = reason ?? string.Empty;
        }

        public long BoothItemId { get; }
        public AssetRowState State { get; }

        /// <summary>収載されていれば item レコード、そうでなければ null。</summary>
        public FeedItem Item { get; }

        /// <summary>規約チャンクを読めていれば terms、そうでなければ null。</summary>
        public FeedTerms Terms { get; }

        /// <summary>直前の取得でこの行の規約が改訂されたか (FeedDiff.ChangedTermsIds 由来)。</summary>
        public bool Revised { get; }

        /// <summary>読めない時の理由 (データ層の言葉をそのまま通す)。</summary>
        public string Reason { get; }

        /// <summary>一覧に出す名前。引けない時も商品 ID だけは必ず見せる (行を消さない)。</summary>
        public string DisplayName =>
            Item != null && !string.IsNullOrEmpty(Item.Name) ? Item.Name : "商品 ID " + BoothItemId;

        public string Shop => Item != null ? (Item.Shop ?? string.Empty) : string.Empty;

        /// <summary>一覧の「規約状態」列。とおり、可否を宣告しない。</summary>
        public string StatusLabel
        {
            get
            {
                switch (State)
                {
                    case AssetRowState.Listed: return "規約あり";
                    case AssetRowState.NotListed: return "未収載";
                    case AssetRowState.TermsUnavailable: return "規約を取得できていません";
                    default: return "収載の有無を確認できません";
                }
            }
        }

        /// <summary>未収載の時だけ Web 側の収載リクエストへ案内する。</summary>
        public bool ShowListingRequestLink => State == AssetRowState.NotListed;

        /// <summary>詳細ペインを組み立てられるか。</summary>
        public bool HasDetail => Terms != null;

        public TermsDetailView BuildDetail() =>
            Terms == null ? null : TermsDetailView.From(Terms, Item);
    }

    /// <summary>フィード全体の鮮度と縮退状態。</summary>
    public sealed class FeedStatusView
    {
        internal FeedStatusView(bool cacheUsable, string cacheFailureReason, int? daysSinceLastFetch,
                                string dataAsOf, FeedRefreshOutcome? lastOutcome, string lastReason,
                                IReadOnlyList<string> rejectedChunks)
        {
            CacheUsable = cacheUsable;
            CacheFailureReason = cacheFailureReason ?? string.Empty;
            DaysSinceLastFetch = daysSinceLastFetch;
            DataAsOf = dataAsOf ?? string.Empty;
            LastOutcome = lastOutcome;
            LastReason = lastReason ?? string.Empty;
            RejectedChunks = rejectedChunks ?? new List<string>();
        }

        public bool CacheUsable { get; }
        public string CacheFailureReason { get; }
        public int? DaysSinceLastFetch { get; }
        public string DataAsOf { get; }
        public FeedRefreshOutcome? LastOutcome { get; }
        public string LastReason { get; }

        /// <summary>署名検証に落ちて取り込まなかったチャンク。空でなければ画面に出す。</summary>
        public IReadOnlyList<string> RejectedChunks { get; }

        /// <summary>
        /// 「最終取得: N 日前」。**判らない時に「今日」と言わない** — DaysSinceLastFetch は
        /// 時計が巻き戻っている時に null を返す契約なので、それをそのまま正直に出す。
        /// </summary>
        public string FreshnessLabel
        {
            get
            {
                if (!DaysSinceLastFetch.HasValue) return "最終取得: 不明";
                int days = DaysSinceLastFetch.Value;
                if (days <= 0) return "最終取得: 今日";
                return "最終取得: " + days + " 日前";
            }
        }

        /// <summary>画面上部の警告帯。問題が無ければ空。</summary>
        public string Banner
        {
            get
            {
                if (!CacheUsable)
                {
                    return CacheFailureReason.Length > 0
                        ? "規約データを読み込めません: " + CacheFailureReason
                        : "規約データを読み込めません";
                }
                if (RejectedChunks.Count > 0)
                {
                    // 汚染データは取り込んでいない = 表示中のものは前回の検証済みキャッシュ。
                    return "署名を検証できなかったデータが " + RejectedChunks.Count +
                           " 件あります。取り込まず、前回の内容を表示しています";
                }
                if (LastOutcome.HasValue)
                {
                    switch (LastOutcome.Value)
                    {
                        case FeedRefreshOutcome.Unreachable:
                            return "フィードに接続できませんでした。保存済みの内容を表示しています (" +
                                   FreshnessLabel + ")";
                        case FeedRefreshOutcome.SignatureRejected:
                            return "署名を検証できませんでした。取り込まず、前回の内容を表示しています";
                        case FeedRefreshOutcome.RepairFailed:
                            return "不足しているデータを取得できませんでした。表示できない項目があります";
                    }
                }
                return string.Empty;
            }
        }

        public bool HasBanner => Banner.Length > 0;
    }

    /// <summary>規約改訂の通知。</summary>
    public sealed class RevisionNoticeView
    {
        private readonly List<AssetRowView> _revisedRows;

        internal RevisionNoticeView(List<AssetRowView> revisedRows, int changedTermsCount, int addedChunkCount)
        {
            _revisedRows = revisedRows;
            ChangedTermsCount = changedTermsCount;
            AddedChunkCount = addedChunkCount;
        }

        /// <summary>改訂された規約を参照している、利用者の資産の行。</summary>
        public IReadOnlyList<AssetRowView> RevisedRows => _revisedRows;

        /// <summary>改訂された terms の総数 (利用者の資産が参照していないものも含む)。</summary>
        public int ChangedTermsCount { get; }

        /// <summary>新しく収載されたチャンク数。**改訂とは別に数える** (FeedDiff の規律と同じ)。</summary>
        public int AddedChunkCount { get; }

        /// <summary>
        /// **利用者の資産に改訂があれば、総数の集計がどうであれ通知する**。
        /// 2 つの数はどちらも同じ ChangedTermsIds から出るので実運用では一致するが、
        /// 一致を前提にすると、集計側が壊れた時に「自分の資産の規約が変わった」という
        /// 最も重い通知が黙って消える。片方だけで成立する条件にしておく。
        /// </summary>
        public bool HasRevisions => ChangedTermsCount > 0 || _revisedRows.Count > 0;

        /// <summary>
        /// 改訂の要約。**「あなたの資産のうち何件か」を先に言う** — 総数だけ出すと、
        /// 自分に関係するのかが判らず、結局全部開くことになる。
        /// </summary>
        public string Summary
        {
            get
            {
                if (!HasRevisions) return string.Empty;
                if (_revisedRows.Count == 0)
                    return "規約が改訂されました (" + ChangedTermsCount + " 件)。あなたの資産が参照しているものはありません";

                string summary = "あなたの資産のうち " + _revisedRows.Count + " 件で規約が改訂されました";
                // 総数が自分の件数を下回る集計は信用できないので、その時は括弧を付けない
                // (誤った総数を並記するくらいなら、確かな方だけを出す)。
                if (ChangedTermsCount >= _revisedRows.Count)
                    summary += " (改訂 " + ChangedTermsCount + " 件)";
                return summary;
            }
        }
    }

    /// <summary>
    /// 表示層の入口。EditorWindow はここが返した値を描くだけで、判断を持たない。
    /// </summary>
    public static class AssetListView
    {
        /// <summary>
        /// 手動リンク済みの商品 ID 列から一覧を組み立てる。
        /// </summary>
        /// <param name="client">データ層。</param>
        /// <param name="linkedItemIds">利用者がリンクした BOOTH 商品 ID (入力順を保つ)。</param>
        /// <param name="changedTermsIds">直前の取得で改訂された terms の id 断片 (無ければ null)。</param>
        public static IReadOnlyList<AssetRowView> BuildRows(
            FeedClient client, IEnumerable<long> linkedItemIds, IReadOnlyList<string> changedTermsIds)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            var rows = new List<AssetRowView>();
            if (linkedItemIds == null) return rows;

            var seen = new HashSet<long>();
            foreach (long id in linkedItemIds)
            {
                // 同じ商品を 2 回リンクしても行は 1 つ (重複は利用者の操作で普通に起きる)。
                if (!seen.Add(id)) continue;

                FeedItem item;
                string reason;
                FeedItemLookup lookup = client.TryGetItem(id, out item, out reason);

                if (lookup == FeedItemLookup.NotListed)
                {
                    rows.Add(new AssetRowView(id, AssetRowState.NotListed, null, null, false, reason));
                    continue;
                }
                if (lookup != FeedItemLookup.Found || item == null)
                {
                    rows.Add(new AssetRowView(id, AssetRowState.Unavailable, null, null, false, reason));
                    continue;
                }

                FeedTerms terms;
                string termsReason;
                FeedItemLookup termsLookup = client.TryGetTerms(item.TermsRef, out terms, out termsReason);
                bool revised = IsRevised(item.TermsRef, changedTermsIds);

                if (termsLookup == FeedItemLookup.Found && terms != null)
                    rows.Add(new AssetRowView(id, AssetRowState.Listed, item, terms, revised, string.Empty));
                else
                    rows.Add(new AssetRowView(id, AssetRowState.TermsUnavailable, item, null, revised, termsReason));
            }
            return rows;
        }

        /// <summary>フィードの鮮度・縮退状態。refresh を呼んでいない時は result に null を渡す。</summary>
        public static FeedStatusView BuildStatus(FeedClient client, FeedRefreshResult result)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            CachedFeed cached = client.GetCachedFeed();
            string failure = client.CacheFailureReason;
            string dataAsOf = cached != null && cached.Index != null ? cached.Index.DataAsOf : string.Empty;

            return new FeedStatusView(
                cached != null,
                failure,
                client.DaysSinceLastFetch(),
                dataAsOf,
                result != null ? result.Outcome : (FeedRefreshOutcome?)null,
                result != null ? result.Reason : string.Empty,
                result != null ? result.RejectedChunks : null);
        }

        /// <summary>改訂通知を組み立てる。</summary>
        public static RevisionNoticeView BuildRevisionNotice(
            IReadOnlyList<AssetRowView> rows, FeedDiff diff)
        {
            var revised = new List<AssetRowView>();
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].Revised) revised.Add(rows[i]);
            }
            int changed = diff != null ? diff.ChangedTermsIds.Count : 0;
            int added = diff != null ? diff.AddedChunks.Count : 0;
            return new RevisionNoticeView(revised, changed, added);
        }

        /// <summary>
        /// terms_ref ("sha256:&lt;hex&gt;") が改訂一覧に含まれるか。
        ///
        /// FeedDiff.ChangedTermsIds はチャンクのファイル名の幹 (短い hex) なので、
        /// **前方一致で照合する**。長さの前提を置かないのは、短縮の桁数が
        /// フィード側の都合で変わりうるため。
        /// </summary>
        private static bool IsRevised(string termsRef, IReadOnlyList<string> changedTermsIds)
        {
            if (changedTermsIds == null || changedTermsIds.Count == 0) return false;
            if (string.IsNullOrEmpty(termsRef)) return false;

            // 幹の形は id の種類で違う: sha256 はコロン以降の hex、visual (v1.3) は
            // "visual-" + slug (FeedPath.TermsChunkPath と同じ規則)。
            // visual をここで hex 扱いすると幹 "visual-…" と前方一致せず、改訂が静かに落ちる。
            string hex;
            if (termsRef.StartsWith("visual:", StringComparison.Ordinal))
            {
                hex = "visual-" + termsRef.Substring("visual:".Length);
            }
            else
            {
                hex = termsRef;
                int colon = hex.IndexOf(':');
                if (colon >= 0) hex = hex.Substring(colon + 1);
            }
            if (hex.Length == 0) return false;

            for (int i = 0; i < changedTermsIds.Count; i++)
            {
                string id = changedTermsIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                // 空文字列が全てに前方一致してしまう事故を上の長さ検査で塞いである。
                if (hex.StartsWith(id, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
