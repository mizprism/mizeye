// 規約 1 本を引き当てる経路の end-to-end (取得 → 署名検証 → キャッシュ → item → terms)。
//
// FeedTermsTests がパーサ単体を実レコードで測るのに対し、こちらは**配線**を測る。
// 分けて要るのは、この経路にパーサとは別の壊れ方があるため:
//
//   - チャンクのパスは terms_id の**先頭 8 桁**でしか決まらない (FeedPath.TermsChunkPath)。
//     照合を抜くと、先頭が同じ別の規約を「その規約」として見せる
//   - item の terms_ref と terms チャンクの terms_id が繋がっていること自体、
//     どちらの単体テストも見ていない
//
// fixture は tools/fixtures/feed-terms (実レコード RadDollV3 を配信形にしたもの)。
// **秘密鍵は署名の直後に破棄済み**なので、ここでも新しい署名は作れない。

using System;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedTermsLookupTests
    {
        private const string TermsId =
            "sha256:18de8a92141d8560295c847adce39779f29bebb577d1240e1a9e5ea79ff6ebac";
        private const long ItemId = 3741802;

        private static readonly DateTimeOffset Start =
            new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        private sealed class Harness : IDisposable
        {
            internal Harness()
            {
                Temp = new TempDirectory();
                var transport = new FakeFeedTransport("v1", FeedFixtures.TermsFixtureDir());
                Client = new FeedClient(transport, new FeedCache(Temp.Root),
                                        FeedFixtures.TermsKeyring(), new FakeClock(Start));
                FeedRefreshResult result = Client.Refresh(manual: false);
                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Empty(result.RejectedChunks);
            }

            internal TempDirectory Temp { get; }
            internal FeedClient Client { get; }

            public void Dispose() => Temp.Dispose();
        }

        [Fact]
        public void AnItemLeadsToItsTermsThroughTheFeed()
        {
            using (var h = new Harness())
            {
                FeedItem item;
                string reason;
                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetItem(ItemId, out item, out reason));
                Assert.Equal(TermsId, item.TermsRef);

                // 表示層が実際に踏む経路: item から取った terms_ref をそのまま渡す。
                FeedTerms terms;
                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetTerms(item.TermsRef, out terms, out reason));
                Assert.Null(reason);
                Assert.Equal(TermsId, terms.TermsId);
                Assert.Equal("RadDollV3利用規約", terms.Title);
                Assert.Equal("まおー", terms.RightsHolder);
            }
        }

        [Fact]
        public void TheTwoLayersArriveSeparatelyThroughTheWholePath()
        {
            using (var h = new Harness())
            {
                FeedTerms terms;
                string reason;
                Assert.Equal(FeedItemLookup.Found, h.Client.TryGetTerms(TermsId, out terms, out reason));

                // この規約は特記事項が O と P を覆う (matrix_overrides 1 件)。行字義は allowed の
                // ままで、実効値だけが conditional になる — 片方に畳まれていたらここで割れる。
                Assert.Equal("allowed", terms.PermissionMatrix["O_video_streaming_broadcast"].Value);
                Assert.Equal("conditional", terms.PermissionMatrixEffective["O_video_streaming_broadcast"]);
                Assert.Equal("allowed", terms.PermissionMatrix["P_publishing"].Value);
                Assert.Equal("conditional", terms.PermissionMatrixEffective["P_publishing"]);

                // 覆われていない行は両層で一致する (覆いが全体に漏れていないこと)。
                Assert.Equal("allowed", terms.PermissionMatrix["A_individual_use"].Value);
                Assert.Equal("allowed", terms.PermissionMatrixEffective["A_individual_use"]);

                // 原文照合の手がかりが行字義側に残っていること (実効値側には引用が無い)。
                Assert.False(string.IsNullOrEmpty(terms.PermissionMatrix["O_video_streaming_broadcast"].Quote));
                Assert.Single(terms.MatrixOverrides);
                Assert.Equal("conditional", terms.MatrixOverrides[0].To);
                Assert.Contains("O_video_streaming_broadcast", terms.MatrixOverrides[0].Keys);

                // 導出属性は実効値の側と揃う (行字義の allowed をそのまま出さない)。
                Assert.Equal("conditional", terms.AttributesDerived["monetized_streaming"]);

                // 原文への案内と、構造化しきれなかった論点が届いていること。
                Assert.Equal(3, terms.Sources.Count);
                Assert.Equal("primary", terms.Sources[0].Role);
                Assert.Equal(4, terms.UnclearPoints.Count);
            }
        }

        [Fact]
        public void AskingForATermsThatSharesTheFirstEightDigitsIsNotAHit()
        {
            using (var h = new Harness())
            {
                // 先頭 8 桁だけ同じ別の terms_id。チャンクのパスは一致するので、
                // 中身の terms_id を照合していなければ「見つかった」と答えてしまう。
                string lookalike = "sha256:18de8a92" + new string('0', 56);

                FeedTerms terms;
                string reason;
                Assert.Equal(FeedItemLookup.NotListed, h.Client.TryGetTerms(lookalike, out terms, out reason));
                Assert.Null(terms);
                Assert.NotNull(reason);
            }
        }
    }
}
