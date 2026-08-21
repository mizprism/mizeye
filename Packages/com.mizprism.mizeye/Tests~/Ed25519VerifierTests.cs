// Ed25519 検証の受け入れテスト。
//
// **生成側の署名テストと同じ入力で測る。**
// 同じ不変条件を 2 言語で固定するのが目的なので、ベクタや改竄ケースを片方だけ変えないこと。
//
// 2 系統で測る理由 (Python 側の docstring と同じ):
//   1. RFC 8032 テストベクタ — 実装の正しさを外部の権威に対して測る
//   2. openssl が署名した fixture — 実運用で使う道具との相互運用性を測る
// 1 だけなら openssl の出力形式との齟齬を見逃し、2 だけなら「自分の間違いを自分で
// 再現しているだけ」の可能性が残る。

using System;
using System.IO;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class Ed25519VerifierTests
    {
        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        // ---- (1) RFC 8032 §7.1 ----

        [Theory]
        [InlineData("RFC8032 TEST1",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
        [InlineData("RFC8032 TEST2",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
        public void AcceptsRfc8032Vectors(string name, string pk, string msg, string sig)
        {
            Assert.True(Ed25519Verifier.Verify(Hex(pk), Hex(msg), Hex(sig)), name + ": 正しい署名を拒否した");
        }

        // ---- 改竄は全て落ちること。1 ビット変えた署名が通る実装は「通す実装」であって検証ではない ----

        private const string Pk2 = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
        private const string Sig2 = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";

        private static byte[] Flip(byte[] b, int i)
        {
            var copy = (byte[])b.Clone();
            copy[i] ^= 1;
            return copy;
        }

        [Fact]
        public void RejectsTamperedMessage() =>
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), Hex("73"), Hex(Sig2)));

        [Fact]
        public void RejectsTamperedSignatureRHalf() =>
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), Hex("72"), Flip(Hex(Sig2), 0)));

        [Fact]
        public void RejectsTamperedSignatureSHalf() =>
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), Hex("72"), Flip(Hex(Sig2), 32)));

        [Fact]
        public void RejectsDifferentPublicKey() =>
            Assert.False(Ed25519Verifier.Verify(Flip(Hex(Pk2), 0), Hex("72"), Hex(Sig2)));

        [Fact]
        public void RejectsShortSignature()
        {
            var sig = Hex(Sig2);
            var truncated = new byte[63];
            Buffer.BlockCopy(sig, 0, truncated, 0, 63);
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), Hex("72"), truncated));
        }

        [Fact]
        public void RejectsShortPublicKey()
        {
            var pk = Hex(Pk2);
            var truncated = new byte[31];
            Buffer.BlockCopy(pk, 0, truncated, 0, 31);
            Assert.False(Ed25519Verifier.Verify(truncated, Hex("72"), Hex(Sig2)));
        }

        [Fact]
        public void RejectsNulls()
        {
            Assert.False(Ed25519Verifier.Verify(null, Hex("72"), Hex(Sig2)));
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), null, Hex(Sig2)));
            Assert.False(Ed25519Verifier.Verify(Hex(Pk2), Hex("72"), null));
        }

        // ---- (2) openssl が署名した実物の fixture ----
        //
        // fixture の秘密鍵は生成直後に破棄してあるので、このテストが通る署名を後から
        // 作り直すことはできない (= 本物の署名を測っている)。

        // ルートの探し方は FeedFixtures に 1 つだけ持つ (目印を変えた時に片方だけ直る事故を作らない)。
        private static string RepoRoot() => FeedFixtures.RepoRoot();

        private static string FixturePath(string name) =>
            Path.Combine(RepoRoot(), "tools", "fixtures", "feed-signing", name);

        [Fact]
        public void AcceptsOpensslFixtureSignature()
        {
            byte[] index = File.ReadAllBytes(FixturePath("index.json"));
            byte[] sig = File.ReadAllBytes(FixturePath("index.json.sig"));
            byte[] pk = Hex(KeyringPublicKeyHex());
            Assert.Equal(64, sig.Length);
            Assert.True(Ed25519Verifier.Verify(pk, index, sig), "openssl の実署名を拒否した");
        }

        [Fact]
        public void RejectsTamperedFixtureIndex()
        {
            byte[] index = File.ReadAllBytes(FixturePath("index.json"));
            byte[] sig = File.ReadAllBytes(FixturePath("index.json.sig"));
            byte[] pk = Hex(KeyringPublicKeyHex());

            // Python 側の配線テストと同じ改竄 ("items": 1 → 2)
            string text = System.Text.Encoding.UTF8.GetString(index).Replace("\"items\": 1", "\"items\": 2");
            byte[] tampered = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.NotEqual(index, tampered); // 置換が実際に起きたこと (起きていなければ何も測っていない)
            Assert.False(Ed25519Verifier.Verify(pk, tampered, sig), "改竄された index を通した");
        }

        /// <summary>fixture の keyring から公開鍵 hex を取り出す (最小限のパース — 依存を足さない)。</summary>
        private static string KeyringPublicKeyHex()
        {
            string json = File.ReadAllText(FixturePath("keyring.json"));
            const string marker = "\"public_key_hex\":";
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(i >= 0, "keyring に public_key_hex が無い");
            int start = json.IndexOf('"', i + marker.Length) + 1;
            int end = json.IndexOf('"', start);
            return json.Substring(start, end - start);
        }
    }
}
