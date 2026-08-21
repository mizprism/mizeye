// フィードの署名検証。データ層で唯一 FeedIndex を作れる場所。
//
// 検証対象は **index.json のファイルバイト列そのもの**。
// JSON を読み直して正規化し直さないので、シリアライザの差で検証が割れる余地が無い。
// チャンクは index が持つ hash で担保されるので、署名は index 1 枚に掛ければ全体に効く。
//
// フィードを生成・検証する側にも対応実装がある。
//
// **順序を厳守する**:
//   (1) 未検証バイト列から signing_key_id **だけ**を読む
//   (2) 同梱 keyring を引き、status で門を掛ける
//   (3) 生バイト列に対して Ed25519 検証
//   (4) 通った時にだけ FeedIndex を組む
// (1) で全体をパースはするが、取り出す**意味**を 1 つに限り、その結果は捨てる。未検証の
// パース結果が呼び出し側に渡る経路を作らない (FeedIndex.TryParse は (4) でしか呼ばない)。

using System;
using System.Security.Cryptography;

namespace Mizprism.LicenseLens
{
    public enum FeedVerdict
    {
        /// <summary>
        /// **ゼロ値は「未検証」**。初期化し忘れたフィールドや配列の隙間が
        /// default(FeedVerifyResult) のまま表示層に流れても「検証済み」にならないように、
        /// 安全な側を 0 に置く (FeedKeyStatus.Unknown = 0 / FeedTransportResult.Reachable = false と同じ規律)。
        /// VerifyIndex がこの値を返すことは無い。
        /// </summary>
        Unverified = 0,
        Ok,
        /// <summary>署名が無い (サイドカー index.json.sig が取れていない)。</summary>
        MissingSignature,
        /// <summary>index が signing_key_id を持たない = どの鍵で検証すべきか決まらない。</summary>
        NoSigningKeyId,
        /// <summary>同梱 keyring に無い鍵、または状態を信頼できない鍵。</summary>
        UnknownKey,
        /// <summary>失効した鍵で署名されている。</summary>
        RevokedKey,
        /// <summary>署名が本文と一致しない (改竄・鍵違い・改行変換など)。</summary>
        BadSignature,
        /// <summary>署名は通ったが index の中身が壊れている / 受け入れられない形。</summary>
        Malformed,
        /// <summary>署名は通ったが、フィードの書式がこのパッケージの対応範囲外 (= パッケージが古い)。</summary>
        UnsupportedSchema
    }

    /// <summary>検証結果と、利用者に見せる日本語の理由。</summary>
    public struct FeedVerifyResult
    {
        // 自動プロパティではなく readonly フィールドで持つ: struct のコンストラクタで
        // 自動プロパティに代入するには C# 9 では this() 呼び出しが要り、書き方が回りくどくなる。
        private readonly FeedVerdict _verdict;
        private readonly string _reason;

        internal FeedVerifyResult(FeedVerdict verdict, string reason)
        {
            _verdict = verdict;
            _reason = reason;
        }

        public FeedVerdict Verdict => _verdict;
        public string Reason => _reason;
        public bool IsOk => _verdict == FeedVerdict.Ok;

        public override string ToString() => Verdict + ": " + Reason;
    }

    public static class FeedVerifier
    {
        public const int SignatureLength = 64;

        /// <summary>
        /// index.json のバイト列とサイドカー署名を検証する。Ok の時だけ index が返る。
        /// </summary>
        public static FeedVerifyResult VerifyIndex(byte[] indexBytes, byte[] signature,
                                                   FeedKeyring keyring, out FeedIndex index)
        {
            index = null;

            if (indexBytes == null || indexBytes.Length == 0)
                return new FeedVerifyResult(FeedVerdict.Malformed, "index が空");
            if (signature == null || signature.Length == 0)
                return new FeedVerifyResult(FeedVerdict.MissingSignature, "署名ファイルが無い (index.json.sig)");
            if (signature.Length != SignatureLength)
            {
                // 切り詰め・追記された署名。Ed25519Verifier も長さで落とすが、理由を分けて出す。
                return new FeedVerifyResult(FeedVerdict.BadSignature,
                    "署名の長さが " + SignatureLength + " bytes でない (" + signature.Length + " bytes)");
            }

            // (1) 未検証バイト列から取り出すのは signing_key_id ただ 1 つ。
            JsonValue root;
            string parseError;
            if (!Json.TryParse(indexBytes, out root, out parseError))
            {
                // parseError には未検証データ由来の断片 (重複キー名など) が入り得るので消毒する。
                return new FeedVerifyResult(FeedVerdict.Malformed,
                    "index を JSON として読めない: " + FeedText.Sanitize(parseError));
            }
            if (root.Kind != JsonKind.Object)
                return new FeedVerifyResult(FeedVerdict.Malformed, "index のトップレベルがオブジェクトでない");

            string keyId;
            if (!root.TryGetString("signing_key_id", out keyId) || keyId.Length == 0)
            {
                return new FeedVerifyResult(FeedVerdict.NoSigningKeyId,
                    "index が signing_key_id を持たない (どの鍵で検証すべきか決まらない)");
            }

            // (2) 信頼の起点は同梱 keyring。フィードの自己申告ではない。
            if (keyring == null)
                return new FeedVerifyResult(FeedVerdict.UnknownKey, "信頼する鍵リングが無い");

            FeedKey key = keyring.Find(keyId);
            if (key == null)
            {
                // 未知の鍵は「新しい鍵かもしれない」ではなく拒否。ローテーションは next を
                // 先に配ってから切り替えるので、未知の鍵が正当な状況は無い。
                return new FeedVerifyResult(FeedVerdict.UnknownKey,
                    "keyring に無い鍵で署名されている (" + Abbreviate(keyId) + ")");
            }
            if (key.Status == FeedKeyStatus.Revoked)
            {
                return new FeedVerifyResult(FeedVerdict.RevokedKey,
                    "失効した鍵で署名されている (" + Abbreviate(keyId) + ")");
            }
            if (key.Status != FeedKeyStatus.Current && key.Status != FeedKeyStatus.Next)
            {
                // 状態を読めない鍵 (未知の status 文字列) は使わない。「読めなかったので通す」は
                // 検証ではない。verdict を UnknownKey に寄せるのは、利用者から見た意味が
                // 「この鍵は信頼できない」で同じだから。
                return new FeedVerifyResult(FeedVerdict.UnknownKey,
                    "鍵の状態を信頼できない (" + Abbreviate(keyId) + ")");
            }

            // (3) 生バイト列に対して検証する。ここで JSON を組み直さない。
            if (!Ed25519Verifier.Verify(key.PublicKeyReference, indexBytes, signature))
            {
                return new FeedVerifyResult(FeedVerdict.BadSignature,
                    "署名が index と一致しない (改竄・鍵違い・改行変換のいずれか)");
            }

            // (4) 通った時にだけ意味のある型にする。署名も一緒に持たせる (対応しない組を作らせない)。
            FeedIndex parsed;
            string indexError;
            FeedIndexParse parse = FeedIndex.TryParse(indexBytes, signature, out parsed, out indexError);
            if (parse == FeedIndexParse.UnsupportedSchema)
                return new FeedVerifyResult(FeedVerdict.UnsupportedSchema, indexError);
            if (parse != FeedIndexParse.Ok)
                return new FeedVerifyResult(FeedVerdict.Malformed, indexError);

            index = parsed;
            return new FeedVerifyResult(FeedVerdict.Ok, "検証済み");
        }

        /// <summary>チャンク本体を index の entry と照合する (sha256 と byte 長の両方)。</summary>
        public static bool VerifyChunk(FeedIndex index, string path, byte[] body, out string reason)
        {
            reason = null;
            if (index == null) { reason = "index が無い"; return false; }
            if (string.IsNullOrEmpty(path)) { reason = "チャンクのパスが空"; return false; }

            FeedChunkEntry entry;
            if (!index.TryGetChunk(path, out entry))
            {
                // index に載っていないものは、たとえ hash が合っても読まない。
                reason = "index に載っていないチャンク: " + path;
                return false;
            }
            if (body == null) { reason = "チャンクの中身が無い: " + path; return false; }

            if (body.Length != entry.Bytes)
            {
                // 長さも見るのは、hash の前段で切り詰めを安く落とすため (hash だけでも検出できるが、
                // index が長さを宣言している以上、両方合わなければ「同じもの」ではない)。
                reason = "チャンクの byte 長が index と一致しない (" + path + "): index=" +
                         entry.Bytes + " actual=" + body.Length;
                return false;
            }

            string actual;
            using (var sha256 = SHA256.Create())
            {
                actual = "sha256:" + FeedHex.Encode(sha256.ComputeHash(body));
            }
            if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            {
                // 縮退系: 汚染データを表示するくらいなら何も表示しない。
                reason = "チャンクの hash が index と一致しない (" + path + ")\n" +
                         "  index=" + entry.Sha256 + "\n  actual=" + actual;
                return false;
            }
            return true;
        }

        /// <summary>未検証の key_id を人に見せる形にする (制御文字を落としてから切り詰める)。</summary>
        private static string Abbreviate(string keyId) => FeedText.Sanitize(keyId, 19);
    }
}
