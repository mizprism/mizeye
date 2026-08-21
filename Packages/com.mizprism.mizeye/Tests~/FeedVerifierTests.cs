// フィード署名検証の受け入れテスト (fixture = tools/fixtures/feed-cache)。
//
// 秘密鍵は破棄済みなので**新しい署名は作れない**。したがって「通るケース」は 1 種類 (実物) で、
// 残りは既存バイト列を壊す方向のケースになる。これは制約であると同時に、通るケースが
// 本物の署名を測っている証拠でもある (Ed25519VerifierTests と同じ立て付け)。

using System;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedVerifierTests
    {
        private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        private static FeedVerifyResult Verify(byte[] index, byte[] signature, FeedKeyring keyring,
                                               out FeedIndex parsed) =>
            FeedVerifier.VerifyIndex(index, signature, keyring, out parsed);

        [Fact]
        public void DefaultResultIsNotVerified()
        {
            // **ゼロ値が「検証済み」であってはならない**。初期化し忘れたフィールドや配列の隙間が
            // 表示層に流れた時、default が Ok だと未検証データが「検証済み」として表示される。
            FeedVerifyResult zero = default(FeedVerifyResult);
            Assert.False(zero.IsOk);
            Assert.Equal(FeedVerdict.Unverified, zero.Verdict);
            Assert.Equal(0, (int)FeedVerdict.Unverified);

            var results = new FeedVerifyResult[3]; // 配列の既定値も同じであること
            for (int i = 0; i < results.Length; i++) Assert.False(results[i].IsOk);
        }

        [Fact]
        public void VerifiedIndexCarriesItsOwnSignature()
        {
            // 署名が裸の byte[] で持ち回られると、index と対応しない組を作れてしまう
            // (実測: v1 の index + v2 の署名でキャッシュが書け、以後ロードが毎回失敗した)。
            FeedIndex index;
            Assert.True(FeedVerifier.VerifyIndex(FeedFixtures.Bytes("v1", "index.json"),
                FeedFixtures.Bytes("v1", "index.json.sig"), FeedFixtures.Keyring(), out index).IsOk);

            Assert.Equal(FeedFixtures.Bytes("v1", "index.json.sig"), index.Signature);

            byte[] borrowed = index.Signature; // 複製であること (外から壊せない)
            borrowed[0] ^= 0xFF;
            Assert.Equal(FeedFixtures.Bytes("v1", "index.json.sig"), index.Signature);
        }

        [Fact]
        public void DoesNotEchoControlCharactersFromUnverifiedInput()
        {
            // signing_key_id は**署名を確かめる前**に読む値なので、攻撃者が中身を決められる。
            // JSON のエスケープを解くと本物の改行になるため、そのままログに出すと行を偽装できる。
            byte[] hostile = Utf8("{\n \"signing_key_id\": \"" +
                                  "sha256:aa\\nFAKE: 検証済み\\r偽の行\\u0007\"\n}\n");
            FeedIndex parsed;
            FeedVerifyResult result = Verify(hostile, FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out parsed);

            Assert.Equal(FeedVerdict.UnknownKey, result.Verdict);
            foreach (char c in result.Reason) Assert.False(char.IsControl(c), "理由文に制御文字が漏れた");
            Assert.Contains("…", result.Reason);       // 19 文字で切り詰められている
            Assert.DoesNotContain("偽の行", result.Reason);

            // 重複キー名など、パース失敗の理由に混ざる断片も同じ扱い
            byte[] hostileKey = Utf8("{\"a\\n偽\": 1, \"a\\n偽\": 2}");
            FeedVerifyResult malformed = Verify(hostileKey, FeedFixtures.Bytes("v1", "index.json.sig"),
                                                FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.Malformed, malformed.Verdict);
            foreach (char c in malformed.Reason) Assert.False(char.IsControl(c), "理由文に制御文字が漏れた");
        }

        [Fact]
        public void AcceptsFixtureIndex()
        {
            FeedIndex index;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"),
                                             FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out index);

            Assert.Equal(FeedVerdict.Ok, result.Verdict);
            Assert.NotNull(index);
            Assert.Equal("0.1", index.FeedSchemaVersion);
            Assert.Equal("2026-08-01T00:00:00Z", index.DataAsOf);
            Assert.Equal(16, index.ItemShards);
            Assert.Equal(2L, index.ItemCount);
            Assert.Equal(2L, index.TermsCount);
            Assert.Equal(4, index.Chunks.Count);
            Assert.Equal("sha256:be78e5995d27fa3ef3b21e80491b714544dfd97db1d56405841ed22b62c91e79",
                         index.SigningKeyId);
            Assert.Equal(new[] { "1.2" }, index.RecordSchemaVersions);

            // RawBytes は署名対象そのもの (複製が返ること = 外から壊せないこと)
            Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), index.RawBytes);
            byte[] first = index.RawBytes;
            first[0] ^= 0xFF;
            Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), index.RawBytes);
        }

        [Fact]
        public void RejectsTamperedIndex()
        {
            byte[] index = FeedFixtures.Bytes("v1", "index.json");
            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.FlipByte(index, index.Length / 2),
                                             FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.BadSignature, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsTamperedSignature()
        {
            byte[] signature = FeedFixtures.Bytes("v1", "index.json.sig");
            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"),
                                             FeedFixtures.FlipByte(signature, 0),
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.BadSignature, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsTruncatedSignature()
        {
            byte[] signature = FeedFixtures.Bytes("v1", "index.json.sig");
            var truncated = new byte[63];
            Buffer.BlockCopy(signature, 0, truncated, 0, truncated.Length);

            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"), truncated,
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.BadSignature, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsMissingSignature()
        {
            FeedIndex parsed;
            Assert.Equal(FeedVerdict.MissingSignature,
                Verify(FeedFixtures.Bytes("v1", "index.json"), null, FeedFixtures.Keyring(), out parsed).Verdict);
            Assert.Equal(FeedVerdict.MissingSignature,
                Verify(FeedFixtures.Bytes("v1", "index.json"), new byte[0], FeedFixtures.Keyring(), out parsed).Verdict);
        }

        [Fact]
        public void RejectsKeyThatIsNotInTheKeyring()
        {
            // **同梱 keyring で fixture の署名を検証する** = 未知の鍵。信頼の起点は同梱 keyring
            // であってフィードの自己申告ではないので、「新しい鍵かも」とは扱わない。
            FeedKeyring production;
            string error;
            Assert.True(FeedKeyringEmbedded.TryLoad(out production, out error), error);

            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"),
                                             FeedFixtures.Bytes("v1", "index.json.sig"), production, out parsed);
            Assert.Equal(FeedVerdict.UnknownKey, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsRevokedKey()
        {
            FeedKeyring revoked = KeyringWithStatus("revoked");
            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"),
                                             FeedFixtures.Bytes("v1", "index.json.sig"), revoked, out parsed);
            Assert.Equal(FeedVerdict.RevokedKey, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsUnknownKeyStatus()
        {
            // 状態を読めない鍵で「読めなかったので通す」にしない (fail closed)。
            FeedKeyring unknown = KeyringWithStatus("retired");
            FeedIndex parsed;
            FeedVerifyResult result = Verify(FeedFixtures.Bytes("v1", "index.json"),
                                             FeedFixtures.Bytes("v1", "index.json.sig"), unknown, out parsed);
            Assert.Equal(FeedVerdict.UnknownKey, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsIndexWithoutSigningKeyId()
        {
            FeedIndex parsed;
            FeedVerifyResult result = Verify(Utf8("{\n \"chunks\": [],\n \"item_shards\": 16\n}\n"),
                                             FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.NoSigningKeyId, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsMalformedIndexJson()
        {
            FeedIndex parsed;
            FeedVerifyResult result = Verify(Utf8("{ \"signing_key_id\": "),
                                             FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.Malformed, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void RejectsCrlfConvertedIndex()
        {
            // **2026-08-13 に windows-latest で実際に起きた事故の回帰**。.gitattributes の穴で
            // checkout が LF→CRLF 変換を掛け、署名対象のバイト列が変わって検証が落ちた。
            // 同じ罠がキャッシュの読み書きにもあるので、データ層でも赤くなるようにしておく。
            byte[] index = FeedFixtures.Bytes("v1", "index.json");
            byte[] crlf = ToCrlf(index);
            Assert.NotEqual(index, crlf);                       // 変換が実際に起きたこと
            Assert.True(crlf.Length > index.Length);

            FeedIndex parsed;
            FeedVerifyResult result = Verify(crlf, FeedFixtures.Bytes("v1", "index.json.sig"),
                                             FeedFixtures.Keyring(), out parsed);
            Assert.Equal(FeedVerdict.BadSignature, result.Verdict);
            Assert.Null(parsed);
        }

        [Fact]
        public void VerifyChunkChecksHashAndLength()
        {
            FeedIndex index = FeedFixtures.VerifiedIndex("v1");
            byte[] body = FeedFixtures.Bytes("v1", "items", "00.json");
            string reason;

            Assert.True(FeedVerifier.VerifyChunk(index, "items/00.json", body, out reason), reason);

            // 1 バイト変えた本文は落ちる
            Assert.False(FeedVerifier.VerifyChunk(index, "items/00.json",
                FeedFixtures.FlipByte(body, 10), out reason));
            Assert.Contains("hash", reason);

            // 長さが違う本文も落ちる (長さだけ・hash だけにしない)
            var shorter = new byte[body.Length - 1];
            Buffer.BlockCopy(body, 0, shorter, 0, shorter.Length);
            Assert.False(FeedVerifier.VerifyChunk(index, "items/00.json", shorter, out reason));
            Assert.Contains("byte 長", reason);

            // index に載っていないパスは、中身が何であれ読まない
            Assert.False(FeedVerifier.VerifyChunk(index, "items/02.json", body, out reason));
            Assert.Contains("index に載っていない", reason);

            // 別のチャンクの本文を入れ替えたものも落ちる (パスと内容の対応が壊れている)
            Assert.False(FeedVerifier.VerifyChunk(index, "items/01.json", body, out reason));
        }

        private static FeedKeyring KeyringWithStatus(string status)
        {
            string text = new UTF8Encoding(false, true).GetString(FeedFixtures.Bytes("keyring.json"))
                .Replace("\"status\": \"current\"", "\"status\": \"" + status + "\"");
            Assert.DoesNotContain("\"status\": \"current\"", text); // 置換が実際に起きたこと

            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyring.TryParse(Utf8(text), out keyring, out error), error);
            return keyring;
        }

        private static byte[] ToCrlf(byte[] bytes)
        {
            var result = new System.Collections.Generic.List<byte>(bytes.Length + 64);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r')) result.Add((byte)'\r');
                result.Add(bytes[i]);
            }
            return result.ToArray();
        }
    }
}
