// index の受け入れ検査 (FeedIndex.TryParse) の直接テスト。
//
// **なぜ internal を直に呼ぶのか**: fixture の秘密鍵は破棄済みで、新しい index に署名を
// 付けられない。したがって「署名は正しいが中身が不正な index」を FeedVerifier 経由で
// 作ることが原理的にできない。ここを測らないと、パス検査を外しても全テストが緑のままに
// なる (実測: 2026-08-14 のミューテーションで、この関門を無効化しても 85 件が全部通った)。
//
// 呼ぶのは検査の対象そのもの (パースの受け入れ規則) であって、署名の順序ではない。
// 順序の担保は FeedVerifierTests + FeedStructureGuardTests が受け持つ。

using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedIndexTests
    {
        private const string ValidChunk =
            "{\"path\": \"items/00.json\", \"sha256\": \"sha256:" +
            "95d7512983e1fd9d24bcb81ce9c9e93ca7febd7a2fdd541cf99be3ef2e7c8b0f\", \"bytes\": 317}";

        private static byte[] Index(string chunks, string extra = "")
        {
            string json =
                "{\n" +
                " \"chunks\": [" + chunks + "],\n" +
                " \"counts\": {\"items\": 1, \"terms\": 1},\n" +
                " \"data_as_of\": \"2026-08-01T00:00:00Z\",\n" +
                " \"feed_schema_version\": \"0.1\",\n" +
                " \"item_shards\": 16,\n" +
                " \"record_schema_versions\": [\"1.2\"],\n" +
                " \"signing_key_id\": \"sha256:be78e5995d27fa3ef3b21e80491b714544dfd97db1d56405841ed22b62c91e79\"" +
                extra + "\n}\n";
            return new UTF8Encoding(false).GetBytes(json);
        }

        /// <summary>fixture の署名 (中身とは対応しないが 64 bytes)。TryParse は署名を検証しない。</summary>
        private static byte[] AnySignature() => FeedFixtures.Bytes("v1", "index.json.sig");

        private static FeedIndexParse Parse(byte[] bytes, out string error)
        {
            FeedIndex index;
            FeedIndexParse result = FeedIndex.TryParse(bytes, AnySignature(), out index, out error);
            Assert.Equal(result == FeedIndexParse.Ok, index != null); // 失敗時に中途半端な index を返さない
            return result;
        }

        private static bool TryParse(byte[] bytes, out string error) =>
            Parse(bytes, out error) == FeedIndexParse.Ok;

        [Fact]
        public void AcceptsWellFormedIndex()
        {
            string error;
            Assert.True(TryParse(Index(ValidChunk), out error), error);
        }

        [Fact]
        public void RejectsTraversalPathAnywhereInTheIndex()
        {
            // **1 行でも不正なら index 全体を拒否する**。「その行だけ捨てて残りを使う」に
            // すると、攻撃者は 1 行の細工で「一部が欠けたフィード」を作れ、欠落が正常系に見える。
            string evil = "{\"path\": \"../../evil.json\", \"sha256\": \"sha256:" +
                          "95d7512983e1fd9d24bcb81ce9c9e93ca7febd7a2fdd541cf99be3ef2e7c8b0f\", \"bytes\": 1}";

            string error;
            Assert.False(TryParse(Index(ValidChunk + ", " + evil), out error));
            Assert.Contains("path", error);

            // Windows の区切り文字・絶対パス・ドライブレターも同じ扱い
            foreach (string bad in new[] { "items\\..\\x.json", "/etc/passwd", "C:\\x.json", "items/00.JSON" })
            {
                string entry = "{\"path\": \"" + bad.Replace("\\", "\\\\") + "\", \"sha256\": \"sha256:" +
                               "95d7512983e1fd9d24bcb81ce9c9e93ca7febd7a2fdd541cf99be3ef2e7c8b0f\", \"bytes\": 1}";
                Assert.False(TryParse(Index(entry), out error), "通してはいけないパスを通した: " + bad);
            }
        }

        [Fact]
        public void RejectsMalformedHashNotation()
        {
            string error;
            // 大文字 16 進 (表記が割れると「一致するのに一致しない」が起きる)
            Assert.False(TryParse(Index("{\"path\": \"items/00.json\", \"sha256\": \"sha256:" +
                "95D7512983E1FD9D24BCB81CE9C9E93CA7FEBD7A2FDD541CF99BE3EF2E7C8B0F\", \"bytes\": 317}"), out error));
            // 接頭辞なし
            Assert.False(TryParse(Index("{\"path\": \"items/00.json\", \"sha256\": \"" +
                "95d7512983e1fd9d24bcb81ce9c9e93ca7febd7a2fdd541cf99be3ef2e7c8b0f\", \"bytes\": 317}"), out error));
            // 桁不足
            Assert.False(TryParse(Index("{\"path\": \"items/00.json\", \"sha256\": \"sha256:95d75129\", \"bytes\": 317}"),
                out error));
        }

        [Fact]
        public void RejectsDuplicateChunkPaths()
        {
            // 同じパスに 2 つの hash があると、どちらで照合しても「正しい」が作れてしまう。
            string error;
            Assert.False(TryParse(Index(ValidChunk + ", " + ValidChunk), out error));
            Assert.Contains("2 度", error);
        }

        [Fact]
        public void RejectsUnusableItemShards()
        {
            string error;
            byte[] zero = new UTF8Encoding(false).GetBytes(
                new UTF8Encoding(false, true).GetString(Index(ValidChunk))
                    .Replace("\"item_shards\": 16", "\"item_shards\": 0"));
            Assert.False(TryParse(zero, out error)); // ゼロ除算を作らせない
            Assert.Contains("item_shards", error);
        }

        [Fact]
        public void RejectsMissingRequiredFields()
        {
            string full = new UTF8Encoding(false, true).GetString(Index(ValidChunk));
            string[] required =
            {
                "\"counts\": {\"items\": 1, \"terms\": 1},\n",
                "\"data_as_of\": \"2026-08-01T00:00:00Z\",\n",
                "\"feed_schema_version\": \"0.1\",\n",
                "\"record_schema_versions\": [\"1.2\"],\n"
            };
            foreach (string field in required)
            {
                Assert.Contains(field, full); // 消す対象が実在すること (何も消さずに緑にしない)
                string error;
                Assert.False(TryParse(new UTF8Encoding(false).GetBytes(full.Replace(field, "")), out error),
                    "必須項目が無い index を通した: " + field);
            }
        }

        [Fact]
        public void RejectsUnsupportedFeedSchemaVersion()
        {
            // 将来 shard の意味やチャンク配置が変わったフィードを古いクライアントが読むと、
            // 該当 shard を見つけられず「未収載」と**断言**する。読めない書式は読めないと言う。
            Assert.Equal(new[] { "0.1" }, FeedIndex.SupportedFeedSchemaVersions);

            string full = new UTF8Encoding(false, true).GetString(Index(ValidChunk));
            foreach (string version in new[] { "0.2", "1.0", "", "0.10" })
            {
                string text = full.Replace("\"feed_schema_version\": \"0.1\"",
                                           "\"feed_schema_version\": \"" + version + "\"");
                Assert.DoesNotContain("\"feed_schema_version\": \"0.1\"", text); // 置換が起きたこと

                string error;
                Assert.Equal(FeedIndexParse.UnsupportedSchema,
                             Parse(new UTF8Encoding(false).GetBytes(text), out error));
                Assert.Contains("パッケージを更新", error);
            }

            // 「壊れている」とは区別される (利用者への意味が違う)
            string malformedError;
            Assert.Equal(FeedIndexParse.Malformed,
                         Parse(new UTF8Encoding(false).GetBytes("{\"feed_schema_version\": 0}"), out malformedError));
        }

        [Fact]
        public void RejectsCallWithoutVerifiedSignature()
        {
            // 署名は検証済み index に同梱される。空で組み立てられるなら、その約束は形だけになる。
            FeedIndex index;
            string error;
            Assert.Equal(FeedIndexParse.Malformed,
                         FeedIndex.TryParse(Index(ValidChunk), null, out index, out error));
            Assert.Equal(FeedIndexParse.Malformed,
                         FeedIndex.TryParse(Index(ValidChunk), new byte[63], out index, out error));
            Assert.Null(index);
        }

        [Fact]
        public void KeepsTheExactBytesItWasGiven()
        {
            byte[] bytes = Index(ValidChunk);
            byte[] signature = AnySignature();
            FeedIndex index;
            string error;
            Assert.Equal(FeedIndexParse.Ok, FeedIndex.TryParse(bytes, signature, out index, out error));
            Assert.Equal(bytes, index.RawBytes);      // 署名対象は「読み直して組み直したもの」ではない
            Assert.Equal(signature, index.Signature); // 署名は index と一緒に運ばれる
        }
    }
}
