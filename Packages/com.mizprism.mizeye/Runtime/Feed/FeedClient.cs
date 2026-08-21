// データ層の統合口。
//
// 縮退の規律がそのまま結果の enum になっている:
//   - フィード到達不可      → Unreachable。**キャッシュは保持**し、「最終取得: N 日前」を出せるようにする
//   - 署名検証失敗          → SignatureRejected。**前のキャッシュに指一本触れない** (汚染データを表示しない)
//   - チャンク単位の失敗    → そのチャンクだけ破棄。残りは成功として進む
//
// レート制限は「上限 1 日 1 回」。**手動更新でも解除しない** — 上限は取得経路ごとでは
// なく合計に掛かる。ボタン連打が BOOTH ではなく自前 CDN に飛ぶだけとはいえ、
// 「クライアントは低レートで取りに行く」という約束をコードで守る。
//
// 門は 2 段ある。1 段では次の 2 つを同時に満たせないため:
//
//   (1) **24 時間の上限**は「成功した取得」で数える (last_success_utc)。失敗を数えると、
//       死んだフィードや壊れたキャッシュのせいで 1 日更新できなくなる。
//   (2) **失敗直後の連打**は短い backoff で止める (last_attempt_utc)。失敗は上限を進めない
//       ので、これが無いと「更新ボタンを押すたびにネットワークへ」になる (実測済みの欠陥)。
//
// さらに、**使えるキャッシュが 1 つも無い時は 24 時間の上限を適用しない**。縮退規律が約束して
// いるのは「到達不可 → キャッシュ表示」であって、「何も出せないまま 24 時間待つ」ではない。
// 上限の目的はネットワーク負荷の抑制であり、見せる物が無い状態はその想定に入っていない。
// (この場合も backoff は効くので、連打は止まる。)
//
// **修復パス**: チャンク単位の失敗は「取得成功」として扱われる (last_success_utc が進む) ので、
// 素直に門 (1) を当てると、落ちた 1 枚はネットワークが 1 秒後に回復しても次の日まで
// `NotAvailable` のままになる。そこで、24 時間の門に当たった時にキャッシュが欠落チャンクを
// 持っていたら、**その欠落分だけを取りに行く**:
//
//   - 手元の検証済み index をそのまま使う (index も署名も取り直さない)。増える通信は欠落枚数ぶんだけ
//   - 門 (1) は掛けない。24 時間は「**新しい世代**を取りに行く」上限であって、取り損ねた 1 枚を
//     埋め直す修復は新しい世代の取得ではない (フィード全体を引き直すわけでもない)
//   - 門 (2) の backoff は掛ける。連打を止める門は修復でも生きている
//   - **last_success_utc は進めない**。新しい世代を取ったわけではないので、進めると修復のたびに
//     24 時間の起点が動いて上限が延びる (last_attempt_utc は記録する = 門 (2) が効く)
//
// 欠落が無ければ修復パスには入らない = 従来どおりネットワークに一切触れずに RateLimited。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    public enum FeedRefreshOutcome
    {
        /// <summary>取得と検証に成功し、前回と差分があった。</summary>
        Updated,
        /// <summary>取得と検証に成功したが、内容は前回と同じだった。</summary>
        NoChange,
        /// <summary>前回取得から 24 時間経っていないので、ネットワークに触れずに戻った。</summary>
        RateLimited,
        /// <summary>フィードに到達できなかった。キャッシュはそのまま。</summary>
        Unreachable,
        /// <summary>署名を検証できなかった。キャッシュはそのまま (汚染データを取り込まない)。</summary>
        SignatureRejected,

        // 以下は修復パス (欠落チャンクだけの部分再取得) の結果。**必ず末尾に足す** —
        // 既定値 0 = Updated が安全側であること (FeedStructureGuardTests.SafeValuesSitAtZero と
        // 同じ規律) を、値をずらして壊さないため。

        /// <summary>欠落していたチャンクを 1 枚以上埋め直した (index は据え置き。残りは RejectedChunks)。</summary>
        ChunksRepaired,
        /// <summary>欠落チャンクを 1 枚も埋め直せなかった。キャッシュはそのまま。</summary>
        RepairFailed
    }

    public sealed class FeedRefreshResult
    {
        private readonly List<string> _rejectedChunks;

        internal FeedRefreshResult(FeedRefreshOutcome outcome, FeedDiff diff, List<string> rejectedChunks,
                                   string reason, DateTimeOffset? nextAllowedUtc, DateTimeOffset? lastFetchUtc)
        {
            Outcome = outcome;
            Diff = diff;
            _rejectedChunks = rejectedChunks ?? new List<string>();
            Reason = reason;
            NextAllowedUtc = nextAllowedUtc;
            LastFetchUtc = lastFetchUtc;
        }

        public FeedRefreshOutcome Outcome { get; }

        /// <summary>前回キャッシュとの差分 (取得できなかった時は null)。</summary>
        public FeedDiff Diff { get; }

        /// <summary>取得または照合に失敗して取り込まなかったチャンク。</summary>
        public IReadOnlyList<string> RejectedChunks => _rejectedChunks;

        /// <summary>利用者に見せる日本語の理由。</summary>
        public string Reason { get; }

        /// <summary>次に取得してよい時刻 (判っている場合)。</summary>
        public DateTimeOffset? NextAllowedUtc { get; }

        /// <summary>この呼び出し後の最終取得時刻。</summary>
        public DateTimeOffset? LastFetchUtc { get; }
    }

    /// <summary>item 引きの結果。「未収載」と「規約不明 / 引けない」を混同しない。</summary>
    public enum FeedItemLookup
    {
        Found,
        /// <summary>フィードに収載されていない (Web 側の収載リクエスト導線へ案内する)。</summary>
        NotListed,
        /// <summary>キャッシュまたはチャンクが手元に無く、収載の有無を言えない。</summary>
        NotAvailable
    }

    public sealed class FeedClient
    {
        /// <summary>「上限 1 日 1 回」。**成功した取得**の間隔に掛かる。</summary>
        public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// 失敗した取得のあと、次に取りに行くまでの最短間隔。
        /// 24 時間の上限は成功でしか進まないので、失敗続きのフィードに対する連打はここで止める。
        /// </summary>
        public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

        private const string IndexPath = "index.json";
        private const string SignaturePath = "index.json.sig";

        private readonly IFeedTransport _transport;
        private readonly FeedCache _cache;
        private readonly FeedKeyring _keyring;
        private readonly IClock _clock;

        private CachedFeed _cached;
        private bool _loadAttempted;
        private string _loadFailureReason;

        public FeedClient(IFeedTransport transport, FeedCache cache, FeedKeyring keyring, IClock clock)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (keyring == null) throw new ArgumentNullException(nameof(keyring));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _transport = transport;
            _cache = cache;
            _keyring = keyring;
            _clock = clock;
        }

        /// <summary>いま使えるキャッシュ (無ければ null)。初回に読み込みと再検証を行う。</summary>
        public CachedFeed GetCachedFeed() => EnsureLoaded();

        /// <summary>キャッシュを使えない理由 (使える時は null)。</summary>
        public string CacheFailureReason
        {
            get
            {
                EnsureLoaded();
                return _loadFailureReason;
            }
        }

        /// <summary>
        /// 最後に成功した取得から何日経ったか (「最終取得: N 日前」)。判らなければ null。
        ///
        /// **時計が巻き戻っている時 (記録が未来) は 0 ではなく null**。0 は「今日取得済み」を
        /// 意味するので、凍結したフィードを「最新」として見せてしまう。判らない時は判らないと言う。
        /// </summary>
        public int? DaysSinceLastFetch()
        {
            DateTimeOffset? last = _cache.ReadMeta().LastSuccessUtc;
            if (!last.HasValue) return null;

            DateTimeOffset now = _clock.UtcNow;
            if (last.Value > now) return null; // 時計異常 (BIOS 電池切れ・VM の RTC ずれ)
            return (int)Math.Floor((now - last.Value).TotalDays);
        }

        /// <param name="manual">利用者が更新ボタンを押した取得か (エディタ起動時の自動取得は false)。</param>
        public FeedRefreshResult Refresh(bool manual)
        {
            DateTimeOffset now = _clock.UtcNow;
            CachedFeed previous = EnsureLoaded();
            FeedCacheMeta meta = _cache.ReadMeta();
            DateTimeOffset? lastSuccess = meta.LastSuccessUtc;

            // 門 (1): 24 時間の上限。**使える表示物がある時だけ**掛ける。
            // 時計が巻き戻って記録が未来になっている場合は「判らない」として取得を許す
            // (掛けると、次回可能が数ヶ月先になって更新が凍結する)。
            bool haveUsableCache = previous != null;
            bool recordIsInTheFuture = lastSuccess.HasValue && lastSuccess.Value > now;
            if (haveUsableCache && lastSuccess.HasValue && !recordIsInTheFuture &&
                now - lastSuccess.Value < MinimumRefreshInterval)
            {
                // 取り損ねた 1 枚があるなら、上限に当たったからといって次の日まで放置しない。
                // 24 時間は「新しい世代を取りに行く」上限であって、欠落の埋め直しには掛からない
                // (門 (2) の backoff は修復パスの中で掛ける)。
                if (previous.MissingChunks.Count > 0) return RepairMissingChunks(previous, meta, now);

                // **ここでネットワークに一切触らない**。触ってから捨てるのでは上限の意味がない。
                DateTimeOffset nextAllowed = lastSuccess.Value + MinimumRefreshInterval;
                string reason = "前回の取得から 24 時間経っていない (次回可能: " +
                                FeedCache.FormatUtc(nextAllowed) + ")" +
                                (manual ? " — 手動更新でも上限は同じ" : "");
                return new FeedRefreshResult(FeedRefreshOutcome.RateLimited, null, null,
                                             reason, nextAllowed, lastSuccess);
            }

            // 門 (2): 直前の試行からの backoff。失敗は上限 (1) を進めないので、これが無いと
            // 死んだフィードへの連打とエディタ再起動が無制限にネットワークへ出る。
            DateTimeOffset? lastAttempt = meta.LastAttemptUtc;
            if (lastAttempt.HasValue && lastAttempt.Value <= now && now - lastAttempt.Value < FailureBackoff)
            {
                DateTimeOffset nextAllowed = lastAttempt.Value + FailureBackoff;
                return new FeedRefreshResult(FeedRefreshOutcome.RateLimited, null, null,
                    "直前の取得試行から間が空いていない (次回可能: " + FeedCache.FormatUtc(nextAllowed) + ")",
                    nextAllowed, lastSuccess);
            }

            // **ネットワークに触れる前に試行を記録する**。ここで記録しないと、失敗して早期に
            // return する経路 (到達不可 / 署名不一致) が門 (2) を素通りする。
            string attemptError;
            _cache.TryRecordAttempt(now, out attemptError); // 記録できなくても取得自体は続ける

            FeedTransportResult indexResult = _transport.Get(IndexPath);
            if (!indexResult.Reachable)
            {
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, null,
                    "フィードに到達できない: " + FeedText.Sanitize(indexResult.Error), null, lastSuccess);
            }
            FeedTransportResult signatureResult = _transport.Get(SignaturePath);
            if (!signatureResult.Reachable)
            {
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, null,
                    "署名を取得できない: " + FeedText.Sanitize(signatureResult.Error), null, lastSuccess);
            }

            FeedIndex index;
            FeedVerifyResult verdict = FeedVerifier.VerifyIndex(indexResult.Body, signatureResult.Body,
                                                               _keyring, out index);
            if (!verdict.IsOk)
            {
                // 検証できないフィードは読まない。ここで return する = キャッシュには何も書かない。
                return new FeedRefreshResult(FeedRefreshOutcome.SignatureRejected, null, null,
                    "検証できないフィードは取り込まない: " + verdict.Reason, null, lastSuccess);
            }

            var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var rejected = new List<string>();
            var rejectionReasons = new List<string>();

            for (int i = 0; i < index.Chunks.Count; i++)
            {
                FeedChunkEntry entry = index.Chunks[i];
                FeedTransportResult chunkResult = _transport.Get(entry.Path);
                string why = null; // 到達できなかった場合は VerifyChunk が呼ばれない (短絡)
                if (chunkResult.Reachable &&
                    FeedVerifier.VerifyChunk(index, entry.Path, chunkResult.Body, out why))
                {
                    bodies[entry.Path] = chunkResult.Body;
                    continue;
                }

                rejected.Add(entry.Path);
                rejectionReasons.Add(chunkResult.Reachable ? why : entry.Path + ": " + FeedText.Sanitize(chunkResult.Error));

                // **落ちたチャンクは前のキャッシュの版を残す**。内容アドレスなので、そのチャンクが
                // 今回改訂されていなければ前の版はそのまま新 index を満たす — 転送事故で
                // 手元の正しいデータを失わない。改訂されていた場合は満たさないので、
                // そのチャンクは欠落として残す (古い本文を新しい index の顔で見せない)。
                byte[] previousBody;
                if (previous != null && previous.TryGetChunkReference(entry.Path, out previousBody))
                {
                    string ignored;
                    if (FeedVerifier.VerifyChunk(index, entry.Path, previousBody, out ignored))
                        bodies[entry.Path] = previousBody;
                }
            }

            string storeError;
            if (!_cache.TryStore(index, bodies, now, out storeError))
            {
                // 結果の enum に「保存できなかった」は無い。利用者から見た意味は Unreachable と
                // 同じ (更新できず、前のキャッシュのまま) なのでそこに寄せ、理由で区別する。
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, rejected,
                    storeError, null, lastSuccess);
            }

            // 書いたものを読み戻して再検証する。「書けた」と「読める」は別物なので、
            // 成功を名乗る前に確かめる。
            _loadAttempted = false;
            CachedFeed stored = EnsureLoaded();
            if (stored == null)
            {
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, rejected,
                    "キャッシュを書いた直後に読み戻せない: " + _loadFailureReason, null, lastSuccess);
            }

            FeedDiff diff = FeedDiff.Between(previous == null ? null : previous.Index, index);
            FeedRefreshOutcome outcome = diff.IsEmpty ? FeedRefreshOutcome.NoChange : FeedRefreshOutcome.Updated;

            string summary = diff.IsEmpty ? "フィードは前回から変わっていない"
                                          : "フィードを更新した (追加 " + diff.AddedChunks.Count +
                                            " / 改訂 " + diff.ChangedChunks.Count +
                                            " / 削除 " + diff.RemovedChunks.Count + ")";
            if (rejected.Count > 0)
                summary += "\n取り込めなかったチャンク " + rejected.Count + " 件:\n  " +
                           string.Join("\n  ", rejectionReasons.ToArray());

            return new FeedRefreshResult(outcome, diff, rejected, summary, now + MinimumRefreshInterval, now);
        }

        /// <summary>
        /// 欠落しているチャンクだけを取り直す (index は手元の検証済みのものを使い、取り直さない)。
        ///
        /// 24 時間の門に当たった時にだけ呼ばれる。**新しい世代の取得ではない**ので、成功しても
        /// last_success_utc は動かさない — 動かすと、修復のたびに次の世代を取りに行ける時刻が
        /// 後ろへずれる (欠落が続くフィードでフィード全体の更新が止まる)。
        /// </summary>
        private FeedRefreshResult RepairMissingChunks(CachedFeed previous, FeedCacheMeta meta, DateTimeOffset now)
        {
            DateTimeOffset? lastSuccess = meta.LastSuccessUtc;

            // 門 (2) は修復でも掛ける。取り損ねた 1 枚が取れない状態は続きやすいので、
            // これが無いと「更新ボタンを押すたびに欠落枚数ぶんの通信」になる。
            DateTimeOffset? lastAttempt = meta.LastAttemptUtc;
            if (lastAttempt.HasValue && lastAttempt.Value <= now && now - lastAttempt.Value < FailureBackoff)
            {
                DateTimeOffset backoffUntil = lastAttempt.Value + FailureBackoff;
                return new FeedRefreshResult(FeedRefreshOutcome.RateLimited, null, null,
                    "直前の取得試行から間が空いていない (次回可能: " + FeedCache.FormatUtc(backoffUntil) + ")",
                    backoffUntil, lastSuccess);
            }

            // 全体取得と同じく、ネットワークに触れる前に試行を記録する。
            string attemptError;
            _cache.TryRecordAttempt(now, out attemptError);

            FeedIndex index = previous.Index; // 署名検証を通った手元の index。取り直さない
            var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var failures = new List<string>();
            IReadOnlyList<string> targets = previous.MissingChunks;

            for (int i = 0; i < targets.Count; i++)
            {
                string path = targets[i];
                FeedTransportResult chunkResult = _transport.Get(path);
                string why = null; // 到達できなかった場合は VerifyChunk が呼ばれない (短絡)
                if (chunkResult.Reachable && FeedVerifier.VerifyChunk(index, path, chunkResult.Body, out why))
                {
                    bodies[path] = chunkResult.Body;
                    continue;
                }
                failures.Add(chunkResult.Reachable ? why : path + ": " + FeedText.Sanitize(chunkResult.Error));
            }

            DateTimeOffset retryAfter = now + FailureBackoff;
            if (bodies.Count == 0)
            {
                return new FeedRefreshResult(FeedRefreshOutcome.RepairFailed, null, new List<string>(targets),
                    "欠落したチャンクを埋め直せなかった " + targets.Count + " 件:\n  " +
                    string.Join("\n  ", failures.ToArray()), retryAfter, lastSuccess);
            }

            // index も meta も書き換えない (= 新しい世代を commit しない)。増えるのは本文だけ。
            string storeError;
            if (!_cache.TryStoreChunks(index, bodies, out storeError))
            {
                // 利用者から見た意味は「更新できず前のキャッシュのまま」= Unreachable と同じなので
                // そこに寄せ、理由で区別する (全体取得の保存失敗と同じ扱い)。
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, new List<string>(targets),
                    storeError, retryAfter, lastSuccess);
            }

            // 「書けた」と「読める」は別物なので、埋まったと言う前に読み戻して確かめる。
            _loadAttempted = false;
            CachedFeed stored = EnsureLoaded();
            if (stored == null)
            {
                return new FeedRefreshResult(FeedRefreshOutcome.Unreachable, null, new List<string>(targets),
                    "キャッシュを書いた直後に読み戻せない: " + _loadFailureReason, retryAfter, lastSuccess);
            }

            // 何が残っているかは、取得の成否ではなく**読み戻したキャッシュ**を正とする。
            var stillMissing = new List<string>(stored.MissingChunks);
            string summary = "欠落していたチャンクを " + (targets.Count - stillMissing.Count) + " 件埋め直した" +
                             (stillMissing.Count == 0 ? "" :
                              " (残り " + stillMissing.Count + " 件:\n  " +
                              string.Join("\n  ", failures.ToArray()) + ")");
            DateTimeOffset? nextAllowed = stillMissing.Count > 0
                ? retryAfter                                    // まだ埋めるものがある = 次は修復
                : lastSuccess + MinimumRefreshInterval;         // 埋まった = 次は新しい世代の取得
            return new FeedRefreshResult(FeedRefreshOutcome.ChunksRepaired, null, stillMissing,
                                         summary, nextAllowed, lastSuccess);
        }

        /// <summary>booth_item_id で 1 件引く。</summary>
        public FeedItemLookup TryGetItem(long boothItemId, out FeedItem item, out string reason)
        {
            item = null;
            reason = null;

            CachedFeed cached = EnsureLoaded();
            if (cached == null)
            {
                reason = "使えるキャッシュが無い" + (_loadFailureReason == null ? "" : ": " + _loadFailureReason);
                return FeedItemLookup.NotAvailable;
            }

            string path = FeedPath.ItemChunkPath(boothItemId, cached.Index.ItemShards);
            if (path == null)
            {
                reason = "item id からチャンクのパスを決められない (" + boothItemId + ")";
                return FeedItemLookup.NotAvailable;
            }

            FeedChunkEntry entry;
            if (!cached.Index.TryGetChunk(path, out entry))
            {
                // フィードを生成する側は空シャードを配信しない (「404 が正しい応答」)。つまり index に
                // 無い shard = そこに収載が 1 件も無い、であって「取れていない」ではない。
                reason = "未収載: booth_item_id=" + boothItemId + " はこのフィードに含まれていない (収載 " +
                         cached.Index.ItemCount + " 件, data_as_of=" + cached.Index.DataAsOf + ")";
                return FeedItemLookup.NotListed;
            }

            byte[] body;
            if (!cached.TryGetChunkReference(path, out body))
            {
                reason = "チャンクを取得できていない (" + path + ") — 収載の有無を言えない";
                return FeedItemLookup.NotAvailable;
            }

            JsonValue root;
            string parseError;
            if (!Json.TryParse(body, out root, out parseError) || root.Kind != JsonKind.Object)
            {
                reason = "チャンクを JSON として読めない (" + path + "): " + parseError;
                return FeedItemLookup.NotAvailable;
            }

            JsonValue items;
            if (!root.TryGetArray("items", out items))
            {
                reason = "チャンクに items 配列が無い (" + path + ")";
                return FeedItemLookup.NotAvailable;
            }

            for (int i = 0; i < items.Items.Count; i++)
            {
                long id;
                if (!items.Items[i].TryGetInt64("booth_item_id", out id) || id != boothItemId) continue;

                FeedItem parsed;
                string itemError;
                if (!FeedItem.TryParse(items.Items[i], out parsed, out itemError))
                {
                    reason = "収載レコードを読めない (" + path + "): " + itemError;
                    return FeedItemLookup.NotAvailable;
                }
                item = parsed;
                return FeedItemLookup.Found;
            }

            reason = "未収載: booth_item_id=" + boothItemId + " はこのフィードに含まれていない (収載 " +
                     cached.Index.ItemCount + " 件, data_as_of=" + cached.Index.DataAsOf + ")";
            return FeedItemLookup.NotListed;
        }

        /// <summary>
        /// terms_ref ("sha256:…") で規約 1 本を引く。item から <see cref="FeedItem.TermsRef"/> を
        /// 取って渡す想定。
        ///
        /// 結果の enum は item 引きと共用する (<see cref="FeedItemLookup"/>) — 区別すべき 3 状態
        /// (引けた / このフィードに無い / 手元に無くて言えない) が terms でも同じだからで、
        /// terms 専用の enum を足すと呼び出し側が同じ分岐を 2 通り書くことになる。
        /// </summary>
        public FeedItemLookup TryGetTerms(string termsRef, out FeedTerms terms, out string reason)
        {
            terms = null;
            reason = null;

            CachedFeed cached = EnsureLoaded();
            if (cached == null)
            {
                reason = "使えるキャッシュが無い" + (_loadFailureReason == null ? "" : ": " + _loadFailureReason);
                return FeedItemLookup.NotAvailable;
            }

            string path = FeedPath.TermsChunkPath(termsRef);
            if (path == null)
            {
                reason = "terms_ref からチャンクのパスを決められない (" + FeedText.Sanitize(termsRef) + ")";
                return FeedItemLookup.NotAvailable;
            }

            FeedChunkEntry entry;
            if (!cached.Index.TryGetChunk(path, out entry))
            {
                reason = "未収載: この terms はフィードに含まれていない (" + FeedText.Sanitize(termsRef) +
                         ", 収載 " + cached.Index.TermsCount + " 本, data_as_of=" + cached.Index.DataAsOf + ")";
                return FeedItemLookup.NotListed;
            }

            byte[] body;
            if (!cached.TryGetChunkReference(path, out body))
            {
                reason = "チャンクを取得できていない (" + path + ") — 規約を引けない";
                return FeedItemLookup.NotAvailable;
            }

            JsonValue root;
            string parseError;
            if (!Json.TryParse(body, out root, out parseError) || root.Kind != JsonKind.Object)
            {
                reason = "チャンクを JSON として読めない (" + path + "): " + parseError;
                return FeedItemLookup.NotAvailable;
            }

            // チャンクのパスは terms_id の**先頭 8 桁**でしか決まらない (FeedPath.TermsChunkPath)。
            // 中身の terms_id を照合しないと、先頭が同じ別の規約を「その規約」として見せうる。
            // 内容アドレスなので中身は署名済み index と一致しているが、**求めた物である保証は別**。
            string termsId;
            if (!root.TryGetString("terms_id", out termsId))
            {
                reason = "チャンクに terms_id が無い (" + path + ")";
                return FeedItemLookup.NotAvailable;
            }
            if (!string.Equals(termsId, termsRef, StringComparison.Ordinal))
            {
                reason = "未収載: この terms はフィードに含まれていない (" + FeedText.Sanitize(termsRef) +
                         " を求めたが " + path + " には別の terms_id がある)";
                return FeedItemLookup.NotListed;
            }

            FeedTerms parsed;
            string termsError;
            if (!FeedTerms.TryParse(root, out parsed, out termsError))
            {
                reason = "規約レコードを読めない (" + path + "): " + termsError;
                return FeedItemLookup.NotAvailable;
            }

            terms = parsed;
            return FeedItemLookup.Found;
        }

        private CachedFeed EnsureLoaded()
        {
            if (_loadAttempted) return _cached;
            _loadAttempted = true;

            CachedFeed loaded;
            string reason;
            if (_cache.TryLoad(_keyring, out loaded, out reason))
            {
                _cached = loaded;
                _loadFailureReason = null;
            }
            else
            {
                _cached = null;
                _loadFailureReason = reason;
            }
            return _cached;
        }
    }
}
