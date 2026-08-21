// 表示層 (Runtime/View) のテスト。
//
// **入力は実物のシード 51 件**。模式の JSON を書き起こすと、書いた人の思い込みが入力と
// 期待の両方に入り、語彙のズレ (スキーマが値を足したのにビューアが知らない) が消える。
// FeedTermsTests が同じ理由で実レコードを使っているので、そこに揃える。
//
// ここで守る不変条件は「利用者に出る結論が、規約に書かれている事実からズレないこと」。
// 表示層は判定を返さないので、ズレは常に **黙って情報が減る** 形で出る:
// 属性が並ばない / 知らない値が握り潰される / item 固有条項が共有規約に混ざる。
// いずれも画面上は正常に見えるため、テストでしか赤くならない。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class ViewTests
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private static FeedTerms ParseTerms(byte[] utf8)
        {
            JsonValue root;
            string error;
            Assert.True(Json.TryParse(utf8, out root, out error), error);
            FeedTerms terms;
            Assert.True(FeedTerms.TryParse(root, out terms, out error), error);
            return terms;
        }

        private static FeedTerms ParseTerms(string json) => ParseTerms(Utf8.GetBytes(json));

        /// <summary>
        /// 属性の**欠落**や未知の値は実シードには (今のところ) 無いので、その 2 ケースだけは
        /// 合成レコードで作る。terms_id と permission_matrix は FeedTerms が必須にしている
        /// ので最小限だけ足す — 検証しているのは表示層であってパーサではない。
        /// </summary>
        private static FeedTerms SynthesizedTerms(string attributesDerivedJson) =>
            ParseTerms("{\"terms_id\":\"sha256:aa\"," +
                       "\"permission_matrix\":{\"A_individual_use\":{\"value\":\"allowed\"}}," +
                       "\"attributes_derived\":" + attributesDerivedJson + "}");

        private static FeedItem ParseItem(string json)
        {
            JsonValue root;
            string error;
            Assert.True(Json.TryParse(Utf8.GetBytes(json), out root, out error), error);
            FeedItem item;
            Assert.True(FeedItem.TryParse(root, out item, out error), error);
            return item;
        }

        // ---- 属性の並び ----

        // `EverySeedRecordShowsAllSevenAttributesWithKnownValues` は**本リポには無い**。
        // ビューアの語彙がコーパスに追随しているかは、**コーパスが増える側**でしか赤くならない
        // — 凍結 fixture に対して回すと、新しい属性値が出ても永久に緑のままになる。
        // 収載レコードを生成する側 (非公開のデータパイプライン) が持つ。

        [Fact]
        public void AttributeOrderIsFixedAndDoesNotFollowTheFeed()
        {
            // 表示順がフィードの辞書順に従うと、順序の揺れを利用者が「規約改訂」と読み違える。
            byte[] bytes = FeedFixtures.SeedTermsBytes("45ca9073");
            TermsDetailView view = TermsDetailView.From(ParseTerms(bytes), null);

            var keys = new List<string>();
            for (int i = 0; i < view.Attributes.Count; i++) keys.Add(view.Attributes[i].Key);

            Assert.Equal(new[]
            {
                "commercial_use", "monetized_streaming", "avatar_modification", "commissioned_editing",
                "parts_reuse", "redistribution", "credit_required"
            }, keys);
        }

        [Fact]
        public void AMissingAttributeStillGetsARow()
        {
            // 欠測を「並べない」で処理すると、画面には残った属性だけが並び、利用者はそれを
            // 全体だと読む。欠測は Unknown として**見える形**で残す。
            TermsDetailView view = TermsDetailView.From(
                SynthesizedTerms("{\"commercial_use\":\"allowed\"}"), null);

            Assert.Equal(7, view.Attributes.Count);

            LicenseAttributeView redistribution = null;
            for (int i = 0; i < view.Attributes.Count; i++)
                if (view.Attributes[i].Key == "redistribution") redistribution = view.Attributes[i];

            Assert.NotNull(redistribution);
            Assert.Equal(LicenseValue.Unknown, redistribution.Value);
            Assert.Equal(string.Empty, redistribution.RawValue);
        }

        [Fact]
        public void AnUnknownValueIsShownVerbatimInsteadOfBeingSwallowed()
        {
            // フィードが将来値を足した時、古いビューアがそれを握り潰すと利用者には
            // 「情報が無い」と映る。実際には情報はあってビューアが古いだけなので、
            // 原文の語をそのまま見せる。
            TermsDetailView view = TermsDetailView.From(
                SynthesizedTerms("{\"commercial_use\":\"allowed_with_notice\"}"), null);

            LicenseAttributeView commercial = view.Attributes[0];
            Assert.Equal("commercial_use", commercial.Key);
            Assert.Equal(LicenseValue.Unknown, commercial.Value);
            Assert.Equal("allowed_with_notice", commercial.RawValue);
            Assert.Contains("allowed_with_notice", commercial.ValueLabel);
        }

        [Fact]
        public void EveryAttributeLineCarriesTheConfirmOriginalSuffix()
        {
            // 出力形式そのもの。判定ではなく整理であることを毎行で示す。
            TermsDetailView view = TermsDetailView.From(ParseTerms(FeedFixtures.SeedTermsBytes("45ca9073")), null);
            for (int i = 0; i < view.Attributes.Count; i++)
            {
                Assert.StartsWith("該当条項: ", view.Attributes[i].Line);
                Assert.EndsWith(LicenseVocabulary.ConfirmSuffix, view.Attributes[i].Line);
            }
        }

        [Fact]
        public void UnclearReadsAsAStatedFactNotAsMissingData()
        {
            // unclear は第一級の値。「情報なし」「-」等に落とすと、規約に記載が無いと
            // 判っている事実と、こちらが調べていない事実が同じ見た目になる。
            string label = LicenseVocabulary.ValueLabel(LicenseValue.Unclear, "unclear");
            Assert.Contains("記載なし", label);
            Assert.NotEqual(LicenseVocabulary.ValueLabel(LicenseValue.Unknown, string.Empty), label);
        }

        // ---- item 固有条項 ----

        [Fact]
        public void ItemConditionsAreShownSeparatelyAndNeverFoldedIntoTheSharedTerms()
        {
            // item 固有条項は terms の derived に反映しない (共有の値を個別条項で汚さない)。
            // 畳むと、同じ terms を参照する**他の商品**にまで条件が伝播して見える。
            FeedTerms terms = ParseTerms(FeedFixtures.SeedTermsBytes("45ca9073"));
            FeedItem item = ParseItem(
                "{\"booth_item_id\":123,\"name\":\"テスト\",\"shop\":\"S\",\"shop_subdomain\":\"s\"," +
                "\"url\":\"https://booth.pm/ja/items/123\",\"terms_ref\":\"" + terms.TermsId + "\"," +
                "\"item_conditions\":[{\"matrix_key\":\"O_video_streaming_broadcast\"," +
                "\"note\":\"この商品のみ\",\"quote\":\"この商品に限り…\"}]}");

            TermsDetailView withItem = TermsDetailView.From(terms, item);
            TermsDetailView withoutItem = TermsDetailView.From(terms, null);

            // 属性は item の有無で 1 つも動かない
            Assert.Equal(withoutItem.Attributes.Count, withItem.Attributes.Count);
            for (int i = 0; i < withItem.Attributes.Count; i++)
            {
                Assert.Equal(withoutItem.Attributes[i].Key, withItem.Attributes[i].Key);
                Assert.Equal(withoutItem.Attributes[i].Value, withItem.Attributes[i].Value);
            }

            // 条項は別節として載り、共有 terms の条件一覧にも混ざらない
            Assert.True(withItem.HasItemConditions);
            Assert.Single(withItem.ItemConditions);
            Assert.Equal(withoutItem.Conditions.Count, withItem.Conditions.Count);
            Assert.False(withoutItem.HasItemConditions);

            // 併読が要ることを画面の言葉で示す (属性一覧だけで判断を終えられると誤る)
            Assert.Contains("この商品だけの条項", withItem.ItemConditionsNotice);
            Assert.Equal(string.Empty, withoutItem.ItemConditionsNotice);
        }

        [Fact]
        public void ConditionsKeepTheirQuoteAndPointAtTheRowTheyBelongTo()
        {
            // 監査可能性: 条件には原文の引用と、どの行に付くかが要る。
            FeedTerms terms = ParseTerms(FeedFixtures.SeedTermsBytes("bfffa7aa"));
            TermsDetailView view = TermsDetailView.From(terms, null);

            Assert.NotEmpty(view.Conditions);
            bool sawQuote = false;
            for (int i = 0; i < view.Conditions.Count; i++)
            {
                ConditionView c = view.Conditions[i];
                Assert.NotEqual(string.Empty, c.MatrixKey);
                // ラベルは知らないキーでもキー名を返す (表示から消さない)
                Assert.NotEqual(string.Empty, c.MatrixLabel);
                if (c.HasQuote) sawQuote = true;
            }
            Assert.True(sawQuote, "条件に原文引用が 1 つも無い — 監査経路が切れている");
        }

        [Fact]
        public void TheDetailPaneExposesTheRouteBackToTheOriginalText()
        {
            TermsDetailView view = TermsDetailView.From(ParseTerms(FeedFixtures.SeedTermsBytes("45ca9073")), null);

            Assert.NotEmpty(view.Sources);
            Assert.NotEqual(string.Empty, view.PrimarySourceUrl);
            Assert.NotEqual(string.Empty, view.Confidence);
            Assert.NotEqual(string.Empty, view.ConfidenceBasis); // 確度は根拠と対で出す

            bool primarySeen = false;
            for (int i = 0; i < view.Sources.Count; i++)
            {
                SourceView s = view.Sources[i];
                Assert.NotEqual(string.Empty, s.FetchedAt);
                Assert.NotEqual(string.Empty, s.ContentHash);
                Assert.NotEqual(string.Empty, s.RoleLabel);
                if (s.IsPrimary) primarySeen = true;
            }
            Assert.True(primarySeen, "primary source が無い — 規約本体へ戻れない");
        }

        [Fact]
        public void UnclearPointsSurviveIntoTheView()
        {
            TermsDetailView view = TermsDetailView.From(ParseTerms(FeedFixtures.SeedTermsBytes("05b9a571")), null);
            Assert.True(view.HasUnclearPoints);
            Assert.NotEmpty(view.UnclearPoints);
        }

        // ---- 一覧の状態 ----

        [Fact]
        public void SafeValuesSitAtZero()
        {
            // 初期化し忘れた値が「許可」「収載済み」として表示される経路を作らない
            // (FeedStructureGuardTests.SafeValuesSitAtZero と同じ規律)。
            Assert.Equal(0, (int)LicenseValue.Unknown);
            Assert.Equal(0, (int)AssetRowState.Unavailable);
        }

        [Fact]
        public void NotListedAndUnavailableStayDistinctInTheList()
        {
            // データ層が分けたものを表示で混ぜたら、分けた意味が消える。
            // 「未収載」は言い切れる事実、「確認できません」は何も言えない状態。
            var notListed = new AssetRowView(1, AssetRowState.NotListed, null, null, false, "未収載");
            var unavailable = new AssetRowView(2, AssetRowState.Unavailable, null, null, false, "キャッシュ無し");

            Assert.Equal("未収載", notListed.StatusLabel);
            Assert.True(notListed.ShowListingRequestLink);

            Assert.NotEqual(notListed.StatusLabel, unavailable.StatusLabel);
            // 収載の有無を言えない相手に収載リクエストを案内しない
            Assert.False(unavailable.ShowListingRequestLink);
        }

        [Fact]
        public void ARowThatCannotBeResolvedStillShowsItsItemId()
        {
            // 行ごと消すと、利用者はリンクしたはずの資産が消えたと読む。
            var row = new AssetRowView(4906631, AssetRowState.Unavailable, null, null, false, "");
            Assert.Contains("4906631", row.DisplayName);
            Assert.False(row.HasDetail);
            Assert.Null(row.BuildDetail());
        }

        // ---- 鮮度と縮退 ----

        [Fact]
        public void UnknownFreshnessIsNotReportedAsToday()
        {
            // DaysSinceLastFetch は時計が巻き戻っている時 null を返す契約。ここで 0 扱いに
            // すると、凍結したフィードが「今日取得済み」として出る。
            var unknown = new FeedStatusView(true, null, null, "2026-08-13", null, null, null);
            Assert.Equal("最終取得: 不明", unknown.FreshnessLabel);

            var today = new FeedStatusView(true, null, 0, "2026-08-13", null, null, null);
            Assert.Equal("最終取得: 今日", today.FreshnessLabel);

            var stale = new FeedStatusView(true, null, 12, "2026-08-13", null, null, null);
            Assert.Equal("最終取得: 12 日前", stale.FreshnessLabel);
        }

        [Fact]
        public void DegradedModesAnnounceThatTheShownDataWasNotReplaced()
        {
            // 署名検証失敗はチャンクを破棄して**前回キャッシュを維持**する。
            // 画面が黙っていると、利用者は表示中の内容を最新だと読む。
            var rejected = new FeedStatusView(true, null, 3, "2026-08-13",
                FeedRefreshOutcome.SignatureRejected, "署名検証に失敗", new List<string> { "terms/aa.json" });
            Assert.True(rejected.HasBanner);
            Assert.Contains("署名", rejected.Banner);
            Assert.Contains("前回", rejected.Banner);

            var unreachable = new FeedStatusView(true, null, 5, "2026-08-13",
                FeedRefreshOutcome.Unreachable, "到達不可", null);
            Assert.Contains("保存済み", unreachable.Banner);
            Assert.Contains("5 日前", unreachable.Banner); // 何日前のものを見ているかを併記する

            var healthy = new FeedStatusView(true, null, 0, "2026-08-13",
                FeedRefreshOutcome.NoChange, "変化なし", null);
            Assert.False(healthy.HasBanner);
        }

        [Fact]
        public void AnUnusableCacheIsAnnouncedWithItsReason()
        {
            var broken = new FeedStatusView(false, "index の署名を検証できません", null, "",
                                            null, null, null);
            Assert.True(broken.HasBanner);
            Assert.Contains("index の署名を検証できません", broken.Banner);
        }

        // ---- 改訂通知 ----

        [Fact]
        public void RevisionSummaryLeadsWithTheUsersOwnAssets()
        {
            // 総数だけ出されても自分に関係するか判らず、結局全部開くことになる。
            var mine = new AssetRowView(1, AssetRowState.Listed, null, null, true, "");
            var notMine = new AssetRowView(2, AssetRowState.Listed, null, null, false, "");

            var withMine = AssetListView.BuildRevisionNotice(new List<AssetRowView> { mine, notMine }, null);
            Assert.Single(withMine.RevisedRows);
            Assert.Contains("あなたの資産のうち 1 件", withMine.Summary);
        }

        [Fact]
        public void NoRevisionsMeansNoNotice()
        {
            var notice = AssetListView.BuildRevisionNotice(new List<AssetRowView>(), null);
            Assert.False(notice.HasRevisions);
            Assert.Equal(string.Empty, notice.Summary);
        }

        // ---- 手動リンク ----

        [Fact]
        public void ItemReferencesAreTakenFromTheItemsSegmentNotFromAnyNumber()
        {
            // URL の中の別の数字を商品 ID として拾うと、利用者は**無関係な規約**を
            // 自分の資産のものとして読む。ここは寛容にしてはいけない箇所。
            long id;

            Assert.True(LinkedAssets.TryParseItemReference("https://booth.pm/ja/items/3741802", out id));
            Assert.Equal(3741802, id);

            Assert.True(LinkedAssets.TryParseItemReference(
                "https://booth.pm/ja/items/3741802?variant=123456", out id));
            Assert.Equal(3741802, id); // クエリの数字に引きずられない

            Assert.True(LinkedAssets.TryParseItemReference(
                "https://shop.booth.pm/items/4906631", out id));
            Assert.Equal(4906631, id);

            Assert.True(LinkedAssets.TryParseItemReference("  3741802  ", out id));
            Assert.Equal(3741802, id);

            Assert.False(LinkedAssets.TryParseItemReference("https://booth.pm/ja/", out id));
            Assert.False(LinkedAssets.TryParseItemReference("items/", out id));
            Assert.False(LinkedAssets.TryParseItemReference("0", out id));
            Assert.False(LinkedAssets.TryParseItemReference("-5", out id));
            Assert.False(LinkedAssets.TryParseItemReference("", out id));
            Assert.False(LinkedAssets.TryParseItemReference(null, out id));
        }

        [Fact]
        public void OneUnreadableEntryDoesNotCostTheWholeList()
        {
            // 保存文字列が部分的に壊れた時に全部捨てると、利用者のリンクが黙って消える。
            IReadOnlyList<long> ids = LinkedAssets.Parse("3741802,,not-a-number,4906631,-1,3741802");

            Assert.Equal(new long[] { 3741802, 4906631 }, ids);      // 順序を保ち、重複は畳む
            Assert.Equal("3741802,4906631", LinkedAssets.Format(ids)); // 往復して安定する
            Assert.Empty(LinkedAssets.Parse(null));
            Assert.Equal(string.Empty, LinkedAssets.Format(null));
        }

        // ---- データ層を通した組み立て ----
        //
        // 上の一覧テストは AssetRowView を直に作っているので、**FeedClient から行を組む経路**
        // (状態の振り分け・改訂の照合) はそこでは 1 行も通らない。署名済み fixture を使って
        // 実際のデータ層越しに測る。

        private static readonly DateTimeOffset Start =
            new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// tools/fixtures/feed-terms — **実レコード** RadDollV3 を配信形にした fixture。
        /// Listed に到達できるのはこちらだけ: feed-cache 側の terms チャンクは FeedTerms より
        /// 前に作られていて `permission_matrix` を持たず (キーが `matrix`)、
        /// **秘密鍵が破棄済みで作り直せない**。
        /// </summary>
        private sealed class TermsHarness : IDisposable
        {
            internal const string ItemTermsId =
                "sha256:18de8a92141d8560295c847adce39779f29bebb577d1240e1a9e5ea79ff6ebac";
            internal const long ItemId = 3741802;

            internal TermsHarness()
            {
                Temp = new TempDirectory();
                var transport = new FakeFeedTransport("v1", FeedFixtures.TermsFixtureDir());
                Client = new FeedClient(transport, new FeedCache(Temp.Root),
                                        FeedFixtures.TermsKeyring(), new FakeClock(Start));
            }

            internal TempDirectory Temp { get; }
            internal FeedClient Client { get; }

            public void Dispose() => Temp.Dispose();
        }

        /// <summary>tools/fixtures/feed-cache — 世代 v1/v2 を持つ。収載/未収載の切り替えに使う。</summary>
        private sealed class CacheHarness : IDisposable
        {
            internal CacheHarness(string generation)
            {
                Temp = new TempDirectory();
                Client = new FeedClient(new FakeFeedTransport(generation), new FeedCache(Temp.Root),
                                        FeedFixtures.Keyring(), new FakeClock(Start));
            }

            internal TempDirectory Temp { get; }
            internal FeedClient Client { get; }

            public void Dispose() => Temp.Dispose();
        }

        [Fact]
        public void AListedAssetCarriesItsDetailPaneThroughTheRealClient()
        {
            using (var h = new TermsHarness())
            {
                h.Client.Refresh(manual: false);

                IReadOnlyList<AssetRowView> rows =
                    AssetListView.BuildRows(h.Client, new long[] { TermsHarness.ItemId }, null);

                Assert.Single(rows);
                Assert.Equal(AssetRowState.Listed, rows[0].State);
                Assert.Equal("規約あり", rows[0].StatusLabel);
                Assert.False(rows[0].ShowListingRequestLink);
                Assert.True(rows[0].HasDetail);

                TermsDetailView detail = rows[0].BuildDetail();
                Assert.Equal("RadDollV3利用規約", detail.Title);
                Assert.Equal(7, detail.Attributes.Count);
                Assert.NotEqual(string.Empty, detail.PrimarySourceUrl); // 原文へ戻れる
            }
        }

        [Fact]
        public void BuildRowsSeparatesNotListedFromUnavailable()
        {
            using (var h = new CacheHarness("v1"))
            {
                h.Client.Refresh(manual: false);

                // 1000018 は v1 に無い = 未収載と**言い切れる**
                IReadOnlyList<AssetRowView> rows =
                    AssetListView.BuildRows(h.Client, new long[] { 1000018 }, null);

                Assert.Single(rows);
                Assert.Equal(AssetRowState.NotListed, rows[0].State);
                Assert.True(rows[0].ShowListingRequestLink);
                Assert.False(rows[0].HasDetail);
                // 引けなくても行は消えず、商品 ID で自分がリンクしたものと判る
                Assert.Contains("1000018", rows[0].DisplayName);
            }
        }

        [Fact]
        public void BuildRowsKeepsTheInputOrderAndCollapsesDuplicates()
        {
            using (var h = new CacheHarness("v1"))
            {
                h.Client.Refresh(manual: false);

                IReadOnlyList<AssetRowView> rows =
                    AssetListView.BuildRows(h.Client, new long[] { 1000018, 1000016, 1000018 }, null);

                // 同じ商品を 2 回リンクするのは普通の操作。行は 1 つに畳む
                Assert.Equal(2, rows.Count);
                Assert.Equal(1000018, rows[0].BoothItemId); // 入力順は保つ
                Assert.Equal(1000016, rows[1].BoothItemId);
            }
        }

        [Fact]
        public void ARevisedTermsIsMatchedToTheRowThatReferencesIt()
        {
            using (var h = new TermsHarness())
            {
                h.Client.Refresh(manual: false);

                // 実際の terms_ref から、フィードが使うチャンク名の幹を作って照合させる
                string stem = Path.GetFileNameWithoutExtension(
                    FeedPath.TermsChunkPath(TermsHarness.ItemTermsId));

                IReadOnlyList<AssetRowView> revised =
                    AssetListView.BuildRows(h.Client, new long[] { TermsHarness.ItemId }, new[] { stem });
                Assert.True(revised[0].Revised);

                // 関係ない terms が改訂されても自分の行は光らない
                IReadOnlyList<AssetRowView> untouched =
                    AssetListView.BuildRows(h.Client, new long[] { TermsHarness.ItemId }, new[] { "deadbeef" });
                Assert.False(untouched[0].Revised);

                // 改訂ゼロを「全部改訂」と読まない
                IReadOnlyList<AssetRowView> none =
                    AssetListView.BuildRows(h.Client, new long[] { TermsHarness.ItemId }, new string[0]);
                Assert.False(none[0].Revised);
            }
        }

        [Fact]
        public void AnEmptyCacheReportsThatNothingCanBeSaidAboutListing()
        {
            // 一度も取得していない状態。**「未収載」と言ってはいけない** — 収載の有無を
            // 確認できていないだけで、載っている可能性がある。
            using (var h = new CacheHarness("v1"))
            {
                IReadOnlyList<AssetRowView> rows =
                    AssetListView.BuildRows(h.Client, new long[] { 1000016 }, null);

                Assert.Single(rows);
                Assert.Equal(AssetRowState.Unavailable, rows[0].State);
                Assert.False(rows[0].ShowListingRequestLink);

                FeedStatusView status = AssetListView.BuildStatus(h.Client, null);
                Assert.False(status.CacheUsable);
                Assert.True(status.HasBanner);
                Assert.Equal("最終取得: 不明", status.FreshnessLabel);
            }
        }

        [Fact]
        public void BuildStatusReportsFreshnessAfterASuccessfulFetch()
        {
            using (var h = new CacheHarness("v1"))
            {
                FeedRefreshResult result = h.Client.Refresh(manual: false);

                FeedStatusView status = AssetListView.BuildStatus(h.Client, result);
                Assert.True(status.CacheUsable);
                Assert.Equal("最終取得: 今日", status.FreshnessLabel);
                Assert.NotEqual(string.Empty, status.DataAsOf);
                Assert.False(status.HasBanner); // 健全な取得で警告帯を出さない
            }
        }

        [Fact]
        public void ARowWhoseTermsChunkCannotBeReadIsNotReportedAsNotListed()
        {
            // feed-cache の terms チャンクは FeedTerms として読めない形 (上の TermsHarness の
            // コメント参照)。**収載はされている**ので「未収載」ではなく「規約を取得できていない」。
            // 混ぜると、載っている規約について「載っていない」と配ることになる。
            using (var h = new CacheHarness("v1"))
            {
                h.Client.Refresh(manual: false);

                IReadOnlyList<AssetRowView> rows =
                    AssetListView.BuildRows(h.Client, new long[] { 1000016 }, null);

                Assert.Single(rows);
                Assert.Equal(AssetRowState.TermsUnavailable, rows[0].State);
                Assert.False(rows[0].ShowListingRequestLink);
                Assert.NotNull(rows[0].Item);              // item は読めている
                Assert.Equal("テスト素体 あお", rows[0].DisplayName);
                Assert.False(rows[0].HasDetail);
                Assert.NotEqual(string.Empty, rows[0].Reason);
            }
        }
    }
}
