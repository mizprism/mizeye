// 公開鍵リングの読み取り。
//
// **信頼の起点はクライアント同梱の keyring であって、フィード自身の自己申告ではない**
// したがってここは「フィードが名乗った key_id を引く表」であり、
// 表に無い鍵・失効した鍵・状態が読めない鍵はすべて拒否側に倒す (fail closed)。
//
// フィードを生成する側にも対応実装がある。**同じ不変条件を
// 2 言語で持つのが目的**なので、片方だけ検査を緩めない:
//   - key_id は公開鍵から決定論的に導かれる (sha256)。記載と食い違えば「別の鍵を指している」
//     のと区別できないので読み込み時に落とす
//   - status=current は 1 本まで (ローテーションは next → current を 1 本ずつ)
// 1 点だけ意図的に違えている: **未知の status 文字列を読み込みエラーにせず Unknown として
// 取り込む**。keyring はクライアント同梱物なので、将来の版が新しい status を足した時に
// 「読めないから全部使えない」より「その鍵だけ使えない」の方が安全側に転ぶ。使う側
// (FeedVerifier) が Current / Next 以外を通さないので、fail closed は保たれる。

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Mizprism.LicenseLens
{
    /// <summary>鍵の状態。未知の文字列は Unknown に落ち、検証を通さない。</summary>
    public enum FeedKeyStatus
    {
        Unknown = 0,
        Current,
        Next,
        Revoked
    }

    public sealed class FeedKey
    {
        private readonly byte[] _publicKey;

        internal FeedKey(string keyId, byte[] publicKey, FeedKeyStatus status, string since, string note)
        {
            KeyId = keyId;
            _publicKey = publicKey;
            Status = status;
            Since = since;
            Note = note;
        }

        public string KeyId { get; }
        public FeedKeyStatus Status { get; }
        public string Since { get; }
        public string Note { get; }

        /// <summary>生の公開鍵 32 bytes。呼び出し側に書き換えられないよう複製を返す。</summary>
        public byte[] PublicKey
        {
            get
            {
                var copy = new byte[_publicKey.Length];
                Buffer.BlockCopy(_publicKey, 0, copy, 0, _publicKey.Length);
                return copy;
            }
        }

        internal byte[] PublicKeyReference => _publicKey; // 検証時の複製を省くための内部用
    }

    public sealed class FeedKeyring
    {
        public const int PublicKeyLength = 32;

        private readonly List<FeedKey> _keys;

        private FeedKeyring(List<FeedKey> keys)
        {
            _keys = keys;
        }

        public IReadOnlyList<FeedKey> Keys => _keys;

        /// <summary>keyring JSON を読む。1 つでも整合しない entry があれば keyring 全体を拒否する。</summary>
        public static bool TryParse(byte[] utf8Json, out FeedKeyring keyring, out string error)
        {
            keyring = null;
            error = null;

            JsonValue root;
            string parseError;
            if (!Json.TryParse(utf8Json, out root, out parseError))
            {
                error = "keyring を JSON として読めない: " + parseError;
                return false;
            }
            if (root.Kind != JsonKind.Object)
            {
                error = "keyring のトップレベルがオブジェクトでない";
                return false;
            }

            JsonValue keysArray;
            if (!root.TryGetArray("keys", out keysArray))
            {
                error = "keyring に keys 配列が無い";
                return false;
            }

            var keys = new List<FeedKey>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int currentCount = 0;

            using (var sha256 = SHA256.Create())
            {
                for (int i = 0; i < keysArray.Items.Count; i++)
                {
                    JsonValue entry = keysArray.Items[i];
                    if (entry.Kind != JsonKind.Object)
                    {
                        error = "keys[" + i + "] がオブジェクトでない";
                        return false;
                    }

                    string keyId, hex;
                    if (!entry.TryGetString("key_id", out keyId) || keyId.Length == 0)
                    {
                        error = "keys[" + i + "] に key_id が無い";
                        return false;
                    }
                    if (!entry.TryGetString("public_key_hex", out hex))
                    {
                        error = "keys[" + i + "] に public_key_hex が無い (" + keyId + ")";
                        return false;
                    }

                    byte[] publicKey;
                    if (!FeedHex.TryDecode(hex, out publicKey) || publicKey.Length != PublicKeyLength)
                    {
                        // Ed25519 の公開鍵はちょうど 32 bytes。長さが違うものは鍵ではない。
                        error = "keys[" + i + "] の public_key_hex が 32 bytes にならない (" + keyId + ")";
                        return false;
                    }

                    string derived = "sha256:" + FeedHex.Encode(sha256.ComputeHash(publicKey));
                    if (!string.Equals(keyId, derived, StringComparison.Ordinal))
                    {
                        // 生成側と同じ検査。手書きの id が鍵と食い違っている状態は
                        // 「別の鍵を指している」のと区別できない。
                        error = "keys[" + i + "] の key_id が公開鍵と一致しない\n" +
                                "  記載   " + keyId + "\n  公開鍵から導かれる値 " + derived;
                        return false;
                    }

                    if (!seenIds.Add(keyId))
                    {
                        // 同じ key_id が 2 行あると、状態 (例: revoked と current) のどちらが効くかが
                        // 引き方の順序で決まってしまう。失効が後ろの行にあると黙って無視される。
                        error = "keys[" + i + "] の key_id が重複している (" + keyId + ")";
                        return false;
                    }

                    string statusText;
                    entry.TryGetString("status", out statusText);
                    FeedKeyStatus status = ParseStatus(statusText);
                    if (status == FeedKeyStatus.Current) currentCount++;

                    string since, note;
                    entry.TryGetString("since", out since);
                    entry.TryGetString("note", out note);

                    keys.Add(new FeedKey(keyId, publicKey, status, since, note));
                }
            }

            if (currentCount > 1)
            {
                // 生成側と同じ理由: 2 本同時に current だと「どちらで署名したか」が
                // index からしか判らず、失効時にどちらを切るかも決まらない。
                error = "status=current の鍵が " + currentCount + " 本ある (1 本でなければならない)";
                return false;
            }

            keyring = new FeedKeyring(keys);
            return true;
        }

        /// <summary>key_id で引く。無ければ null (= 信頼しない)。</summary>
        public FeedKey Find(string keyId)
        {
            if (string.IsNullOrEmpty(keyId)) return null;
            for (int i = 0; i < _keys.Count; i++)
            {
                if (string.Equals(_keys[i].KeyId, keyId, StringComparison.Ordinal)) return _keys[i];
            }
            return null;
        }

        private static FeedKeyStatus ParseStatus(string s)
        {
            if (string.Equals(s, "current", StringComparison.Ordinal)) return FeedKeyStatus.Current;
            if (string.Equals(s, "next", StringComparison.Ordinal)) return FeedKeyStatus.Next;
            if (string.Equals(s, "revoked", StringComparison.Ordinal)) return FeedKeyStatus.Revoked;
            return FeedKeyStatus.Unknown;
        }
    }
}
