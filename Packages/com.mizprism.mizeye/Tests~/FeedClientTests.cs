// データ層の統合テスト (取得 → 検証 → キャッシュ → 差分)。
//
// 測っているのは  / 約束そのもの:
//   - 上限 1 日 1 回を**ネットワークに触れずに**守ること
//   - 到達不可でキャッシュを失わないこと
//   - 汚染チャンクを取り込まず、手元の正しい版を失わないこと
//   - 「未収載」と「引けない」を混同しないこと

using System;
using System.Collections.Generic;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedClientTests
    {
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan PastTheLimit = TimeSpan.FromHours(25);

        private sealed class Harness : IDisposable
        {
            internal Harness(string generation)
            {
                Temp = new TempDirectory();
                Transport = new FakeFeedTransport(generation);
                Clock = new FakeClock(Start);
                Cache = new FeedCache(Temp.Root);
                Client = new FeedClient(Transport, Cache, FeedFixtures.Keyring(), Clock);
            }

            internal TempDirectory Temp { get; }
            internal FakeFeedTransport Transport { get; }
            internal FakeClock Clock { get; }
            internal FeedCache Cache { get; }
            internal FeedClient Client { get; private set; }

            /// <summary>新しいセッションを模す (キャッシュだけを引き継ぐ)。</summary>
            internal FeedClient NewSession()
            {
                Client = new FeedClient(Transport, Cache, FeedFixtures.Keyring(), Clock);
                return Client;
            }

            public void Dispose() => Temp.Dispose();
        }

        [Fact]
        public void FirstRefreshFromEmptyCacheAddsEverything()
        {
            using (var h = new Harness("v1"))
            {
                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Empty(result.RejectedChunks);
                Assert.Equal(new[] { "items/00.json", "items/01.json", "terms/11111111.json", "terms/22222222.json" },
                             result.Diff.AddedChunks);
                Assert.Empty(result.Diff.ChangedChunks);
                Assert.Empty(result.Diff.RemovedChunks);
                Assert.True(result.Diff.DataAsOfChanged);
                Assert.False(result.Diff.IsEmpty);
                Assert.Equal(Start, result.LastFetchUtc);
                Assert.Equal(Start + FeedClient.MinimumRefreshInterval, result.NextAllowedUtc);

                // 書いたキャッシュは新しいセッションからも検証を通ること
                CachedFeed cached;
                string reason;
                Assert.True(h.Cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), cached.Index.RawBytes);
            }
        }

        [Fact]
        public void SecondRefreshWithinTwentyFourHoursDoesNotTouchTheNetwork()
        {
            using (var h = new Harness("v1"))
            {
                Assert.Equal(FeedRefreshOutcome.Updated, h.Client.Refresh(manual: false).Outcome);
                int callsAfterFirst = h.Transport.CallCount;
                Assert.True(callsAfterFirst > 0);

                // **手動更新でも上限は同じ**
                FeedRefreshResult result = h.Client.Refresh(manual: true);

                Assert.Equal(FeedRefreshOutcome.RateLimited, result.Outcome);
                Assert.Equal(callsAfterFirst, h.Transport.CallCount); // ネットワークに一切触れていない
                Assert.Equal(Start + FeedClient.MinimumRefreshInterval, result.NextAllowedUtc);
                Assert.Null(result.Diff);

                // 新しいセッション (エディタ再起動) でも上限は効く
                h.Clock.Advance(TimeSpan.FromHours(23));
                Assert.Equal(FeedRefreshOutcome.RateLimited, h.NewSession().Refresh(manual: false).Outcome);
                Assert.Equal(callsAfterFirst, h.Transport.CallCount);
            }
        }

        [Fact]
        public void RevisionIsDetectedAsChangedChunks()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Empty(result.RejectedChunks);
                Assert.Equal(new[] { "terms/11111111.json" }, result.Diff.ChangedChunks);
                Assert.Equal(new[] { "items/02.json" }, result.Diff.AddedChunks);
                Assert.Empty(result.Diff.RemovedChunks);
                Assert.Equal(new[] { "11111111" }, result.Diff.ChangedTermsIds);
                Assert.True(result.Diff.DataAsOfChanged);

                CachedFeed cached = h.Client.GetCachedFeed();
                Assert.Equal("2026-08-05T00:00:00Z", cached.Index.DataAsOf);
                Assert.Equal(3L, cached.Index.ItemCount);
            }
        }

        [Fact]
        public void RefreshingTheSameFeedTwiceReportsNoChange()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);
                h.Clock.Advance(PastTheLimit);

                FeedRefreshResult result = h.Client.Refresh(manual: false);
                Assert.Equal(FeedRefreshOutcome.NoChange, result.Outcome);
                Assert.True(result.Diff.IsEmpty);
            }
        }

        [Fact]
        public void RejectedChunkKeepsThePreviousVersion()
        {
            // 転送事故で本文が壊れて届いたケース。**そのチャンクだけ捨て、手元の版を残す**。
            // 内容アドレスなので、改訂されていないチャンクの前の版は新 index を
            // そのまま満たす — 事故で正しいデータを失わない。
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);
                byte[] good = FeedFixtures.ChunkBytes("v1", "items/00.json");

                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                h.Transport.Override("items/00.json", FeedFixtures.FlipByte(good, 20)); // v1 と v2 で同一のチャンク

                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Equal(new[] { "items/00.json" }, result.RejectedChunks);

                CachedFeed cached = h.Client.GetCachedFeed();
                byte[] body;
                Assert.True(cached.TryGetChunk("items/00.json", out body));
                Assert.Equal(good, body);          // 壊れた本文は書かれていない
                Assert.Empty(cached.MissingChunks);

                // 残りの更新は進んでいること
                Assert.Equal("2026-08-05T00:00:00Z", cached.Index.DataAsOf);

                // 直接 VerifyChunk でも落ちること (FeedClient 経由でしか測らない、にしない)
                string reason;
                Assert.False(FeedVerifier.VerifyChunk(cached.Index, "items/00.json",
                    FeedFixtures.FlipByte(good, 20), out reason));
            }
        }

        [Fact]
        public void RejectedRevisedChunkBecomesMissingInsteadOfStale()
        {
            // 改訂されたチャンクが落ちた場合、前の版は新しい index を満たさない。古い本文を
            // 新しい index の顔で見せるより、欠落として扱う (「未取得」と言える方が正直)。
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                h.Transport.MakeUnreachable("terms/11111111.json");

                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Equal(new[] { "terms/11111111.json" }, result.RejectedChunks);

                CachedFeed cached = h.Client.GetCachedFeed();
                Assert.Equal(new[] { "terms/11111111.json" }, cached.MissingChunks);
                byte[] body;
                Assert.False(cached.TryGetChunk("terms/11111111.json", out body));

                // 他のチャンクは無事で、キャッシュ全体は使える
                Assert.True(cached.TryGetChunk("terms/22222222.json", out body));
                Assert.Equal(FeedFixtures.ChunkBytes("v2", "terms/22222222.json"), body);
            }
        }

        [Fact]
        public void UnreachableFeedKeepsTheCache()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);
                h.Clock.Advance(PastTheLimit);
                h.Transport.AllUnreachable = true;

                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.Unreachable, result.Outcome);
                Assert.Equal(Start, result.LastFetchUtc); // 取得時刻は更新されない (再取得できる)
                Assert.Null(result.Diff);

                // キャッシュは残り、検証も通る
                CachedFeed cached;
                string reason;
                Assert.True(h.Cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), cached.Index.RawBytes);

                // 「最終取得: N 日前」
                Assert.Equal(1, h.Client.DaysSinceLastFetch());
            }
        }

        [Fact]
        public void SignatureRejectionDoesNotTouchTheCache()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);
                byte[] cachedIndexBefore = h.Client.GetCachedFeed().Index.RawBytes;

                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                h.Transport.Override("index.json", FeedFixtures.FlipByte(FeedFixtures.Bytes("v2", "index.json"), 30));

                FeedRefreshResult result = h.Client.Refresh(manual: false);

                Assert.Equal(FeedRefreshOutcome.SignatureRejected, result.Outcome);
                Assert.Contains("検証できないフィードは取り込まない", result.Reason);

                // 前のキャッシュに指一本触れていないこと
                CachedFeed cached;
                string reason;
                Assert.True(h.Cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Equal(cachedIndexBefore, cached.Index.RawBytes);
                Assert.Equal(Start, cached.LastSuccessUtc);
            }
        }

        [Fact]
        public void ItemLookupDistinguishesNotListedFromNotAvailable()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                FeedItem item;
                string reason;
                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetItem(1000016, out item, out reason));
                Assert.Equal("テスト素体 あお", item.Name);
                Assert.Equal("https://booth.pm/ja/items/1000016", item.Url);
                Assert.Equal("sha256:1111111111111111111111111111111111111111111111111111111111111111", item.TermsRef);
                Assert.Equal("terms/11111111.json", FeedPath.TermsChunkPath(item.TermsRef));
                Assert.Empty(item.ItemConditions);

                // v1 に 1000018 は無い → **未収載** (「規約不明」ではない)
                Assert.Equal(FeedItemLookup.NotListed, h.Client.TryGetItem(1000018, out item, out reason));
                Assert.Null(item);
                Assert.Contains("未収載", reason);

                // v2 では収載されている
                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                h.Client.Refresh(manual: false);

                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetItem(1000018, out item, out reason));
                Assert.Equal("テスト素体 あか", item.Name);
                Assert.Single(item.ItemConditions);
                Assert.Equal("A_personal_use", item.ItemConditions[0].MatrixKey);
                Assert.Equal("この商品に限り…", item.ItemConditions[0].Quote);
            }
        }

        [Fact]
        public void ItemLookupReportsNotAvailableWhenTheChunkIsMissing()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                h.Clock.Advance(PastTheLimit);
                h.Transport.UseGeneration("v2");
                h.Transport.MakeUnreachable("items/02.json"); // 新設シャードだけ取れなかった
                h.Client.Refresh(manual: false);

                FeedItem item;
                string reason;
                // index には載っているのに手元に無い = 収載の有無を言えない (未収載と言い切らない)
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetItem(1000018, out item, out reason));
                Assert.Contains("取得できていない", reason);

                // 取れているシャードは引ける
                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetItem(1000016, out item, out reason));
            }
        }

        [Fact]
        public void ItemLookupWithoutCacheIsNotAvailable()
        {
            using (var h = new Harness("v1"))
            {
                FeedItem item;
                string reason;
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetItem(1000016, out item, out reason));
                Assert.Contains("使えるキャッシュが無い", reason);
                Assert.Null(h.Client.DaysSinceLastFetch());
            }
        }

        [Fact]
        public void UnusableCacheIsRefetchedWithoutWaitingADay()
        {
            // 壊れたキャッシュは黙って使わない。そのうえで、**見せる物が無いまま 24 時間待たない**
            // (約束しているのは「キャッシュ表示」であって「何も出せない状態の維持」ではない)。
            // 上限の目的はネットワーク負荷の抑制であり、この状況はその想定に入っていない。
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                string indexPath = System.IO.Path.Combine(h.Temp.Root, "index.json");
                byte[] onDisk = System.IO.File.ReadAllBytes(indexPath);
                System.IO.File.WriteAllBytes(indexPath, FeedFixtures.FlipByte(onDisk, 40));

                FeedClient session = h.NewSession();
                Assert.Null(session.GetCachedFeed());
                Assert.Contains("再検証できない", session.CacheFailureReason);

                // backoff を越えていれば、24 時間以内でも取り直しに行ける
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                FeedRefreshResult result = h.NewSession().Refresh(manual: true);
                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Equal(4, result.Diff.AddedChunks.Count); // 前が無い扱いなので全て追加
                Assert.NotNull(h.NewSession().GetCachedFeed());
            }
        }

        [Fact]
        public void DeadFeedIsBackedOffInsteadOfHammered()
        {
            // 失敗は 24 時間の上限を進めない (進めると死んだフィードで 1 日更新できなくなる)。
            // そのぶん、失敗直後の連打は短い backoff で止める — でないと押すたびに飛ぶ。
            using (var h = new Harness("v1"))
            {
                h.Transport.AllUnreachable = true;

                Assert.Equal(FeedRefreshOutcome.Unreachable, h.Client.Refresh(manual: true).Outcome);
                int callsAfterFirst = h.Transport.CallCount;
                Assert.Equal(1, callsAfterFirst); // index.json で止まる

                for (int i = 0; i < 5; i++)
                {
                    FeedRefreshResult result = h.NewSession().Refresh(manual: true);
                    Assert.Equal(FeedRefreshOutcome.RateLimited, result.Outcome);
                    Assert.Contains("直前の取得試行", result.Reason);
                }
                Assert.Equal(callsAfterFirst, h.Transport.CallCount); // 連打も再起動もネットワークに出ない

                // backoff を越えれば 1 回だけ再挑戦する
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                Assert.Equal(FeedRefreshOutcome.Unreachable, h.NewSession().Refresh(manual: false).Outcome);
                Assert.Equal(callsAfterFirst + 1, h.Transport.CallCount);
            }
        }

        [Fact]
        public void SignatureRejectionIsAlsoBackedOff()
        {
            // 署名が通らないフィードは index と sig の 2 本を取りに行く。ここも記録されないと
            // 連打で 2 倍飛ぶ (実測された欠陥)。
            using (var h = new Harness("v1"))
            {
                h.Transport.Override("index.json",
                    FeedFixtures.FlipByte(FeedFixtures.Bytes("v1", "index.json"), 30));

                Assert.Equal(FeedRefreshOutcome.SignatureRejected, h.Client.Refresh(manual: true).Outcome);
                int calls = h.Transport.CallCount;
                Assert.Equal(2, calls); // index + sig

                Assert.Equal(FeedRefreshOutcome.RateLimited, h.NewSession().Refresh(manual: true).Outcome);
                Assert.Equal(calls, h.Transport.CallCount);
            }
        }

        [Fact]
        public void ClockGoingBackwardsDoesNotFreezeUpdates()
        {
            // BIOS 電池切れ・VM の RTC ずれで「記録が未来」になる。上限を素直に当てると次回可能が
            // 数ヶ月先になって更新が凍結し、しかも「0 日前 (= 今日取得済み)」と表示される —
            // 凍結したフィードを「最新」として見せる最悪の組み合わせ。
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);
                Assert.Equal(0, h.Client.DaysSinceLastFetch());

                h.Clock.UtcNow = Start - TimeSpan.FromDays(60); // 時計が正しい値に戻った
                FeedClient session = h.NewSession();

                Assert.Null(session.DaysSinceLastFetch()); // 「0 日前」ではなく「判らない」
                Assert.Equal(FeedRefreshOutcome.NoChange, session.Refresh(manual: false).Outcome);
                Assert.True(h.Transport.CallCount > 0);
            }
        }

        [Fact]
        public void MissingChunkIsRepairedWithoutWaitingADay()
        {
            // **欠陥 (2) の回帰**。旧テスト RejectedChunkWaitsForTheNextDay は「落ちた 1 枚は次の
            // 24 時間まで再取得されない」を既知の限界として固定していたが、ネットワークが 1 秒後に
            // 回復してもその shard が 1 日 NotAvailable のままになる。24 時間は「新しい世代を
            // 取りに行く」上限であって欠落の埋め直しには掛からない、として期待値を入れ替えた。
            using (var h = new Harness("v1"))
            {
                h.Transport.MakeUnreachable("terms/22222222.json");
                FeedRefreshResult first = h.Client.Refresh(manual: false);
                Assert.Equal(FeedRefreshOutcome.Updated, first.Outcome);
                Assert.Equal(new[] { "terms/22222222.json" }, first.RejectedChunks);

                // 門 (2) の backoff は修復でも生きている (欠落があっても連打はネットワークに出ない)
                h.Transport.Reset();
                FeedRefreshResult tooSoon = h.NewSession().Refresh(manual: true);
                Assert.Equal(FeedRefreshOutcome.RateLimited, tooSoon.Outcome);
                Assert.Contains("直前の取得試行", tooSoon.Reason);
                Assert.Equal(0, h.Transport.CallCount);

                // backoff を越えたら、24 時間を待たずに**欠落分だけ**取りに行く
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                FeedRefreshResult repaired = h.NewSession().Refresh(manual: true);

                Assert.Equal(FeedRefreshOutcome.ChunksRepaired, repaired.Outcome);
                Assert.Empty(repaired.RejectedChunks);
                Assert.Null(repaired.Diff); // 新しい世代を取ったわけではない
                // index も署名も取り直していない = 増える通信は欠落枚数ぶんだけ
                Assert.Equal(new[] { "terms/22222222.json" }, h.Transport.Requested);
                Assert.Empty(h.NewSession().GetCachedFeed().MissingChunks);
                byte[] body;
                Assert.True(h.NewSession().GetCachedFeed().TryGetChunk("terms/22222222.json", out body));
                Assert.Equal(FeedFixtures.ChunkBytes("v1", "terms/22222222.json"), body);

                // **成功時刻は動かない** (動かすと修復のたびに 24 時間の起点が延びる)
                Assert.Equal(Start, h.Cache.ReadMeta().LastSuccessUtc);
                Assert.Equal(Start, repaired.LastFetchUtc);

                // 欠落が無くなれば、24 時間以内の更新は従来どおりネットワークに触れない
                h.Transport.Reset();
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                Assert.Equal(FeedRefreshOutcome.RateLimited, h.NewSession().Refresh(manual: true).Outcome);
                Assert.Equal(0, h.Transport.CallCount);

                // 24 時間の起点は最初の取得のままなので、そこから 1 日で世代ごと取り直せる
                h.Clock.Advance(PastTheLimit);
                Assert.Equal(FeedRefreshOutcome.NoChange, h.NewSession().Refresh(manual: false).Outcome);
                Assert.Empty(h.NewSession().GetCachedFeed().MissingChunks);
            }
        }

        [Fact]
        public void RepairThatFailsKeepsTheCacheAndBacksOff()
        {
            // 修復に行って取れなかった / 壊れて届いた場合。キャッシュはそのまま、欠落もそのまま、
            // 成功時刻も動かない。検証は修復パスでも同じように掛ける (汚染を取り込まない)。
            using (var h = new Harness("v1"))
            {
                h.Transport.MakeUnreachable("terms/22222222.json");
                Assert.Equal(FeedRefreshOutcome.Updated, h.Client.Refresh(manual: false).Outcome);

                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                h.Transport.Reset();
                h.Transport.MakeUnreachable("terms/22222222.json");

                FeedRefreshResult failed = h.NewSession().Refresh(manual: true);
                Assert.Equal(FeedRefreshOutcome.RepairFailed, failed.Outcome);
                Assert.Equal(new[] { "terms/22222222.json" }, failed.RejectedChunks);
                Assert.Equal(new[] { "terms/22222222.json" }, h.Transport.Requested);
                Assert.Equal(Start, failed.LastFetchUtc);
                Assert.Equal(Start, h.Cache.ReadMeta().LastSuccessUtc);
                Assert.Equal(new[] { "terms/22222222.json" }, h.NewSession().GetCachedFeed().MissingChunks);

                // 失敗した修復も試行として記録される = 連打は backoff で止まる
                Assert.Equal(FeedRefreshOutcome.RateLimited, h.NewSession().Refresh(manual: true).Outcome);
                Assert.Equal(1, h.Transport.CallCount);

                // 壊れた本文が届いた場合も取り込まない (欠落のまま)
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                h.Transport.Reset();
                h.Transport.Override("terms/22222222.json",
                    FeedFixtures.FlipByte(FeedFixtures.ChunkBytes("v1", "terms/22222222.json"), 7));

                FeedRefreshResult corrupted = h.NewSession().Refresh(manual: true);
                Assert.Equal(FeedRefreshOutcome.RepairFailed, corrupted.Outcome);
                Assert.Contains("一致しない", corrupted.Reason);
                Assert.Equal(new[] { "terms/22222222.json" }, h.NewSession().GetCachedFeed().MissingChunks);

                // 回復したフィードからは埋められる
                h.Clock.Advance(FeedClient.FailureBackoff + TimeSpan.FromMinutes(1));
                h.Transport.Reset();
                Assert.Equal(FeedRefreshOutcome.ChunksRepaired, h.NewSession().Refresh(manual: true).Outcome);
                Assert.Empty(h.NewSession().GetCachedFeed().MissingChunks);
            }
        }
    }
}
