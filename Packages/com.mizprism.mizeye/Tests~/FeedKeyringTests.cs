// 公開鍵リングの受け入れテスト。
//
// **同梱の鍵リングも読ませる**。fixture だけで測ると「テスト用に
// 都合よく整えた形」しか通らない実装になり、実物の keyring (コメント配列 $comment や
// note / since を含む) を受け付けるかは未検証のまま残る。ここは実物の回帰。

using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedKeyringTests
    {
        private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        private static string FixtureKeyringText() =>
            new UTF8Encoding(false, true).GetString(FeedFixtures.Bytes("keyring.json"));

        [Fact]
        public void ParsesFixtureKeyring()
        {
            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyring.TryParse(FeedFixtures.Bytes("keyring.json"), out keyring, out error), error);
            Assert.Single(keyring.Keys);

            FeedKey key = keyring.Find("sha256:be78e5995d27fa3ef3b21e80491b714544dfd97db1d56405841ed22b62c91e79");
            Assert.NotNull(key);
            Assert.Equal(FeedKeyStatus.Current, key.Status);
            Assert.Equal(32, key.PublicKey.Length); // Ed25519 の公開鍵はちょうど 32 bytes
            Assert.Null(keyring.Find("sha256:0000000000000000000000000000000000000000000000000000000000000000"));
        }

        [Fact]
        public void ParsesTheShippedKeyring()
        {
            // 実物が読めなくなったら、パッケージは本番フィードを 1 つも検証できない。
            //
            // **測る対象は同梱リング (FeedKeyringEmbedded)** — 利用者の手元に届くのはこれで、
            // 生成元の鍵リング原本は配布物に含まれない。「同梱リング == 生成元」の
            // 突き合わせは、原本を持つ側が回す。
            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyringEmbedded.TryLoad(out keyring, out error), error);
            Assert.NotEmpty(keyring.Keys);

            FeedKey current = null;
            for (int i = 0; i < keyring.Keys.Count; i++)
                if (keyring.Keys[i].Status == FeedKeyStatus.Current) current = keyring.Keys[i];
            Assert.NotNull(current); // 署名に使う鍵が 1 本あること (署名側が current の鍵を 1 本選ぶのと同じ前提)
            Assert.Equal(32, current.PublicKey.Length);
        }

        [Fact]
        public void RejectsKeyIdThatDoesNotMatchPublicKey()
        {
            // 公開鍵を 1 文字変えると key_id は導出値と食い違う = 別の鍵を指しているのと区別できない。
            string text = FixtureKeyringText().Replace(
                "\"public_key_hex\": \"e18cf1775b8ba8d3ab5f234d1cdbcc9b5b97c04e6680192fa514c1a75b35584e\"",
                "\"public_key_hex\": \"f18cf1775b8ba8d3ab5f234d1cdbcc9b5b97c04e6680192fa514c1a75b35584e\"");
            Assert.DoesNotContain("e18cf1775b8ba8d3ab5f234d1cdbcc9b5b97c04e6680192fa514c1a75b35584e", text); // 置換が起きたこと

            FeedKeyring keyring;
            string error;
            Assert.False(FeedKeyring.TryParse(Utf8(text), out keyring, out error));
            Assert.Contains("key_id が公開鍵と一致しない", error);
        }

        [Fact]
        public void RejectsPublicKeyOfWrongLength()
        {
            string text = FixtureKeyringText().Replace(
                "e18cf1775b8ba8d3ab5f234d1cdbcc9b5b97c04e6680192fa514c1a75b35584e", "e18cf177");

            FeedKeyring keyring;
            string error;
            Assert.False(FeedKeyring.TryParse(Utf8(text), out keyring, out error));
            Assert.Contains("32 bytes", error);
        }

        [Fact]
        public void UnknownStatusBecomesUnknown()
        {
            // 未知の status は読み込みエラーにしないが、Current / Next でもない = 検証を通さない
            // (FeedVerifierTests.RejectsUnknownKeyStatus が実際に通らないことを測る)。
            string text = FixtureKeyringText().Replace("\"status\": \"current\"", "\"status\": \"retired\"");

            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyring.TryParse(Utf8(text), out keyring, out error), error);
            Assert.Equal(FeedKeyStatus.Unknown, keyring.Keys[0].Status);
        }

        [Fact]
        public void RejectsDuplicateKeyIds()
        {
            // 同じ key_id が 2 行あると、revoked が後ろにある時に黙って無視される。
            string entry =
                "  {\n" +
                "   \"key_id\": \"sha256:be78e5995d27fa3ef3b21e80491b714544dfd97db1d56405841ed22b62c91e79\",\n" +
                "   \"public_key_hex\": \"e18cf1775b8ba8d3ab5f234d1cdbcc9b5b97c04e6680192fa514c1a75b35584e\",\n" +
                "   \"status\": \"revoked\"\n" +
                "  }\n";
            string text = "{\n \"keys\": [\n" + entry + "  ,\n" + entry + " ]\n}\n";

            FeedKeyring keyring;
            string error;
            Assert.False(FeedKeyring.TryParse(Utf8(text), out keyring, out error));
            Assert.Contains("重複", error);
        }

        [Fact]
        public void RejectsMalformedKeyring()
        {
            FeedKeyring keyring;
            string error;
            Assert.False(FeedKeyring.TryParse(Utf8("{ \"keys\": "), out keyring, out error));
            Assert.False(FeedKeyring.TryParse(Utf8("{ \"nokeys\": [] }"), out keyring, out error));
            Assert.False(FeedKeyring.TryParse(null, out keyring, out error));
        }
    }
}
