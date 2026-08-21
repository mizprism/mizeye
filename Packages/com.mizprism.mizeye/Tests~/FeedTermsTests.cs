// terms レコードの読み取り (FeedTerms) と、terms 引き (FeedClient.TryGetTerms) のテスト。
//
// **模式の JSON では測らない**。ここが掴むべき欠陥は「キー名の食い違い」と「層の混同」で、
// どちらもテスト側でレコードを書き起こすと消える (書いた人が同じ思い込みで書くため)。
// したがって入力は**実物のレコード**を使う。
//
// 配信形はシードのレコードに permission_matrix_effective を
// 足した形なので、その層はテスト側で実レコードに接いで作る (下の DeliveryForm)。fixture の
// terms チャンクは署名済みで、**新しい署名を作れない**ため実物の形に差し替えられない
// (tools/fixtures/feed-cache/README.md: 鍵は使い捨てで秘密鍵は破棄済み)。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedTermsTests
    {
        private const string ReferenceRecord = "45ca9073";   // 「しなの」3Dモデル利用規約 (override 無し)
        private const string OverriddenRecord = "18de8a92";  // RadDollV3 利用規約 (2 行を横断 override)

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private static JsonValue Root(byte[] utf8)
        {
            JsonValue root;
            string error;
            Assert.True(Json.TryParse(utf8, out root, out error), error);
            return root;
        }

        private static FeedTerms Parse(byte[] utf8)
        {
            FeedTerms terms;
            string error;
            Assert.True(FeedTerms.TryParse(Root(utf8), out terms, out error), error);
            return terms;
        }

        private static bool TryParse(string json, out FeedTerms terms, out string error)
        {
            JsonValue root;
            string parseError;
            Assert.True(Json.TryParse(Utf8.GetBytes(json), out root, out parseError), parseError);
            return FeedTerms.TryParse(root, out terms, out error);
        }

        /// <summary>
        /// 実レコードに配信形の実効値層を接ぐ (フィード生成器が組むのと同じ形)。
        /// 合成規則は validate_seed.compose_effective の写しで、**テスト側の入力を作るためだけ**の
        /// もの — 実装 (FeedTerms) はこの合成を持たない。持たせるとフィードと消費側で
        /// 実効値の定義が 2 つになる。
        /// </summary>
        private static byte[] DeliveryForm(byte[] recordBytes, FeedTerms literal)
        {
            var effective = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, FeedTermsCell> cell in literal.PermissionMatrix)
                effective[cell.Key] = cell.Value.Value;
            foreach (FeedTermsOverride o in literal.MatrixOverrides)
                foreach (string key in o.Keys)
                    if (effective.ContainsKey(key)) effective[key] = o.To;

            var values = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in effective)
            {
                if (values.Length > 0) values.Append(", ");
                values.Append('"').Append(pair.Key).Append("\": \"").Append(pair.Value).Append('"');
            }

            string block = "\n \"permission_matrix_effective\": {" +
                           "\"_derived\": \"compose_effective(permission_matrix, matrix_overrides) の決定論合成\", " +
                           "\"values\": {" + values + "}},";

            string text = Utf8.GetString(recordBytes);
            int brace = text.IndexOf('{');
            Assert.True(brace >= 0);
            return Utf8.GetBytes(text.Substring(0, brace + 1) + block + text.Substring(brace + 1));
        }

        // `EverySeedRecordParsesAndNothingIsSilentlyDropped` は**本リポには無い**。
        // 「新しいシードレコードが出た時にパーサが行を静かに落としていないか」は、コーパスが
        // 増える側の生きたコーパスでしか赤くならない。凍結 fixture に対して全件テストを書くと、
        // 赤くなる事由が構造的に発生しない。収載レコードを生成する側が持つ。

        [Fact]
        public void TheReferenceRecordExposesTheFieldNamesTheRecordActuallyHas()
        {
            FeedTerms terms = Parse(FeedFixtures.SeedTermsBytes(ReferenceRecord));

            Assert.Equal("sha256:45ca90737e9cf1cf15d43ecb92a0798605af24867567c1cdcccb4317991d0945", terms.TermsId);
            Assert.Equal("「しなの」3Dモデル利用規約", terms.Title);
            Assert.Equal("ぽんでろ (ポンデロニウム研究所)", terms.RightsHolder);
            Assert.Equal("@ぽんでろ", terms.CreditDisplay);
            Assert.Equal("#Shinano3D", terms.RecommendedHashtag);
            Assert.Equal("vn3-template", terms.Dialect);
            Assert.Equal("1.00", terms.TermsVersion);
            Assert.Equal("1.2", terms.SchemaVersion);
            Assert.Equal("high", terms.Confidence);
            Assert.Contains("規約原文 PDF", terms.ConfidenceBasis);
            Assert.Equal("2026-08-07T05:55:00Z", terms.LastVerifiedAt);

            // 実レコードで JSON null になっている任意フィールドは null のまま。既定値を発明しない。
            Assert.Null(terms.ScopeNote);
            Assert.Null(terms.StructuringNotes);
            Assert.Null(terms.RecommendedHashtagNote);
            Assert.Empty(terms.History);

            // 行字義 (原文にそう書いてある値 + 逐語引用)
            Assert.Equal(24, terms.PermissionMatrix.Count);
            FeedTermsCell individual = terms.PermissionMatrix["A_individual_use"];
            Assert.Equal("allowed", individual.Value);
            Assert.Equal("営利・非営利の目的問わず利用を許可します", individual.Quote);
            Assert.Equal("営利・非営利の目的問わず利用を許可", individual.Note);

            FeedTermsCell gamePlatform = terms.PermissionMatrix["D_upload_game_platform"];
            Assert.Equal("allowed", gamePlatform.Value);
            Assert.Null(gamePlatform.Note); // note の無いセルは null (空文字を作らない)

            // **4 値に潰さない**。実物には not_required / note が出ている。
            Assert.Equal("not_required", terms.PermissionMatrix["V_credit"].Value);
            Assert.Equal("note", terms.PermissionMatrix["X_special_notes"].Value);
            Assert.Equal("unclear", terms.PermissionMatrix["R_product_development"].Value);

            Assert.Empty(terms.MatrixOverrides);

            // 属性 (導出) は行字義とは別の入れ物で持つ
            Assert.Equal("allowed", terms.AttributesDerived["commercial_use"]);
            Assert.Equal("forbidden", terms.AttributesDerived["redistribution"]);
            Assert.Equal("not_required", terms.AttributesDerived["credit_required"]);
            Assert.Equal("conditional", terms.AttributesDerived["commissioned_editing"]);

            Assert.Equal(5, terms.Conditions.Count);
            Assert.Equal("X_special_notes", terms.Conditions[0].MatrixKey);
            Assert.Contains("@ponderogen", terms.Conditions[0].Note);
            Assert.NotEmpty(terms.Conditions[0].Quote);

            Assert.Equal(2, terms.Sources.Count);
            Assert.Equal("external_google_drive_pdf", terms.Sources[0].LocationType);
            Assert.Equal("primary", terms.Sources[0].Role);
            Assert.Equal(10, terms.Sources[0].Pages);
            Assert.StartsWith("sha256:", terms.Sources[0].ContentHash);
            Assert.Equal("2026-08-07T05:55:00Z", terms.Sources[0].FetchedAt);
            Assert.Null(terms.Sources[0].Note);

            Assert.Equal("https://booth.pm/ja/items/6106863", terms.Sources[1].Url);
            Assert.Equal("secondary", terms.Sources[1].Role);
            Assert.Null(terms.Sources[1].Pages); // PDF でない出典に頁数を発明しない
            Assert.Contains("canonical text v1", terms.Sources[1].Note);

            Assert.Equal(2, terms.UnclearPoints.Count);
            Assert.Contains("個別問い合わせ", terms.UnclearPoints[0]);
        }

        [Fact]
        public void TheDeliveredFormKeepsTheDerivedLayerApartFromTheLiteralOne()
        {
            byte[] record = FeedFixtures.SeedTermsBytes(OverriddenRecord);
            FeedTerms literal = Parse(record);

            // 前提: この規約は 2 行を横断 override している (実効値と行字義が食い違う実例)
            Assert.Single(literal.MatrixOverrides);
            FeedTermsOverride crossCutting = literal.MatrixOverrides[0];
            Assert.Equal(new[] { "O_video_streaming_broadcast", "P_publishing" }, crossCutting.Keys);
            Assert.Equal("conditional", crossCutting.To);
            Assert.Equal("special_notes", crossCutting.Basis);
            Assert.NotEmpty(crossCutting.Quote);
            Assert.Contains("VTuber", crossCutting.Note);

            FeedTerms delivered = Parse(DeliveryForm(record, literal));

            // 行字義は**そのまま**。実効値で上書きされていない (層を 1 つの辞書に畳んでいない)。
            Assert.Equal("allowed", delivered.PermissionMatrix["O_video_streaming_broadcast"].Value);
            Assert.Equal("allowed", delivered.PermissionMatrix["P_publishing"].Value);
            Assert.NotEmpty(delivered.PermissionMatrix["O_video_streaming_broadcast"].Quote);

            // 実効値は覆われた値になる
            Assert.Equal(delivered.PermissionMatrix.Count, delivered.PermissionMatrixEffective.Count);
            Assert.Equal("conditional", delivered.PermissionMatrixEffective["O_video_streaming_broadcast"]);
            Assert.Equal("conditional", delivered.PermissionMatrixEffective["P_publishing"]);
            Assert.Equal("allowed", delivered.PermissionMatrixEffective["A_individual_use"]);
            Assert.Contains("compose_effective", delivered.EffectiveDerivation);

            // 実効値は引用を持たない (原文照合は行字義側の quote から辿る)
            Assert.NotEmpty(delivered.PermissionMatrix["A_individual_use"].Quote);
        }

        [Fact]
        public void TheDeliveredFormSurfacesTheVerificationMode()
        {
            // v1.3: 配信形は検証様式を top-level "verification" で運ぶ (配信形の terms チャンク)。
            // visual = hash 検証不能 (視覚読解由来) — UI が利用者に可視化するための根拠フィールド
            byte[] record = FeedFixtures.SeedTermsBytes(ReferenceRecord);
            string text = Utf8.GetString(record);
            int brace = text.IndexOf('{');
            Assert.True(brace >= 0);
            byte[] delivered = Utf8.GetBytes(
                text.Substring(0, brace + 1) + "\n \"verification\": \"visual\"," + text.Substring(brace + 1));

            FeedTerms terms = Parse(delivered);
            Assert.Equal("visual", terms.Verification);
        }

        [Fact]
        public void RecordsThatCannotBeShownAreRejected()
        {
            FeedTerms terms;
            string error;

            Assert.False(TryParse("{\"permission_matrix\": {}}", out terms, out error));
            Assert.Contains("terms_id", error);
            Assert.Null(terms);

            Assert.False(TryParse("{\"terms_id\": \"sha256:aa\"}", out terms, out error));
            Assert.Contains("permission_matrix", error);

            // 判定値の無いセルはレコードごと拒否する (空欄で出すと「書いていない」と混ざる)
            Assert.False(TryParse("{\"terms_id\": \"sha256:aa\", \"permission_matrix\": " +
                                  "{\"A_individual_use\": {\"note\": \"…\"}}}", out terms, out error));
            Assert.Contains("value", error);

            Assert.False(TryParse("{\"terms_id\": \"sha256:aa\", \"permission_matrix\": " +
                                  "{\"A_individual_use\": \"allowed\"}}", out terms, out error));
            Assert.Contains("オブジェクトでない", error);

            Assert.False(TryParse("[]", out terms, out error));
            Assert.Contains("オブジェクトでない", error);
        }

        [Fact]
        public void UnknownFieldsAreIgnoredInsteadOfFailingTheWholeRecord()
        {
            // record_schema_versions が上がって未知のフィールドが来ても、古いパッケージが
            // 「読めない」で全滅しない (逆に、必須が欠けたレコードは上のテストで拒否される)。
            byte[] record = FeedFixtures.SeedTermsBytes(ReferenceRecord);
            string text = Utf8.GetString(record);
            int brace = text.IndexOf('{');
            byte[] withUnknown = Utf8.GetBytes(
                text.Substring(0, brace + 1) +
                "\n \"future_layer_v2\": {\"verdict\": \"???\"}, \"jurisdiction\": \"JP\"," +
                text.Substring(brace + 1));

            FeedTerms terms = Parse(withUnknown);
            Assert.Equal(24, terms.PermissionMatrix.Count);
            Assert.Equal("「しなの」3Dモデル利用規約", terms.Title);
        }

        // ---- terms 引き (FeedClient) --------------------------------------------------
        //
        // fixture の terms チャンクは行字義層を持たないミニチュア (署名の作り直しができないため
        // 実物の形にできない)。したがって Found まで通せるのは FeedTerms の直接テストの側で、
        // ここで測るのは**縮退の区別** — 「このフィードに無い」と「手元に無くて言えない」を
        // 混同しないこと。

        private const string FixtureTermsRef =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111";

        private sealed class Harness : IDisposable
        {
            internal Harness(string generation)
            {
                Temp = new TempDirectory();
                Transport = new FakeFeedTransport(generation);
                Clock = new FakeClock(new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero));
                Client = new FeedClient(Transport, new FeedCache(Temp.Root), FeedFixtures.Keyring(), Clock);
            }

            internal TempDirectory Temp { get; }
            internal FakeFeedTransport Transport { get; }
            internal FakeClock Clock { get; }
            internal FeedClient Client { get; }

            public void Dispose() => Temp.Dispose();
        }

        [Fact]
        public void TermsLookupWithoutCacheIsNotAvailable()
        {
            using (var h = new Harness("v1"))
            {
                FeedTerms terms;
                string reason;
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetTerms(FixtureTermsRef, out terms, out reason));
                Assert.Contains("使えるキャッシュが無い", reason);
                Assert.Null(terms);
            }
        }

        [Fact]
        public void TermsLookupDistinguishesNotListedFromNotAvailable()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                FeedTerms terms;
                string reason;

                // 収載されていない terms (チャンクが index に無い) = **未収載**
                Assert.Equal(FeedItemLookup.NotListed, h.Client.TryGetTerms(
                    "sha256:3333333333333333333333333333333333333333333333333333333333333333",
                    out terms, out reason));
                Assert.Contains("未収載", reason);
                Assert.Null(terms);

                // terms_ref の形が壊れていて、そもそもチャンクの場所を決められない
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetTerms("sha256:ab", out terms, out reason));
                Assert.Contains("パスを決められない", reason);

                // **先頭 8 桁だけ一致する別の terms** はチャンクを引けてしまう。中身の terms_id を
                // 照合しないと、求めていない規約を「その規約」として見せることになる。
                Assert.Equal(FeedItemLookup.NotListed, h.Client.TryGetTerms(
                    "sha256:11111111ffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                    out terms, out reason));
                Assert.Contains("別の terms_id", reason);

                // fixture の terms は行字義層を持たない = 表示に足りない → 引けない側に倒す
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetTerms(FixtureTermsRef, out terms, out reason));
                Assert.Contains("規約レコードを読めない", reason);
                Assert.Contains("permission_matrix", reason);
            }
        }

        [Fact]
        public void TermsLookupReportsNotAvailableWhenTheChunkIsMissing()
        {
            using (var h = new Harness("v1"))
            {
                h.Client.Refresh(manual: false);

                // v2 で terms/11111111.json が改訂される。取れなかった場合、v1 の本文は
                // 新しい index の hash を満たさないので手元に残らない (古い本文を新しい顔で見せない)。
                h.Clock.Advance(TimeSpan.FromHours(25));
                h.Transport.UseGeneration("v2");
                h.Transport.MakeUnreachable("terms/11111111.json");
                h.Client.Refresh(manual: false);

                FeedTerms terms;
                string reason;
                Assert.Equal(FeedItemLookup.NotAvailable, h.Client.TryGetTerms(FixtureTermsRef, out terms, out reason));
                Assert.Contains("取得できていない", reason);
            }
        }
    }
}
