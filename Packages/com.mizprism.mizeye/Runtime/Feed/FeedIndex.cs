// 検証済みフィード index の型。
//
// **この型は「署名検証を通ったバイト列」の証拠である**。public なコンストラクタを持たず、
// 生成できるのは FeedVerifier だけ (internal TryParse)。したがって呼び出し側は
// 「未検証のパース済みデータ」を手にできない — 「汚染データを表示しない」を
// 規律ではなく型で担保する。
//
// 逆に言えば、ここに public なコンストラクタや public な TryParse を後から足すと、この
// 保証は静かに消える (どこも赤くならない)。足したくなったら、その前に FeedVerifier を通す
// 経路が本当に無いのかを疑うこと。

using System;
using System.Collections.Generic;

namespace Mizprism.LicenseLens
{
    /// <summary>index の受け入れ結果。「壊れている」と「書式が新しすぎる」は利用者への意味が違う。</summary>
    internal enum FeedIndexParse
    {
        Ok,
        Malformed,
        UnsupportedSchema
    }

    public sealed class FeedChunkEntry
    {
        internal FeedChunkEntry(string path, string sha256, long bytes)
        {
            Path = path;
            Sha256 = sha256;
            Bytes = bytes;
        }

        /// <summary>フィード相対パス (FeedPath.IsValidChunkPath を満たすことが保証されている)。</summary>
        public string Path { get; }

        /// <summary>"sha256:" + 小文字 16 進 64 桁。フィードが hash を書く時と同じ表記。</summary>
        public string Sha256 { get; }

        /// <summary>チャンクのバイト長。hash と併せて照合する (長さだけ・hash だけにしない)。</summary>
        public long Bytes { get; }
    }

    public sealed class FeedIndex
    {
        private const int Sha256HexLength = 64;
        private const string Sha256Prefix = "sha256:";

        /// <summary>
        /// このパッケージが解釈できるフィード書式。**既知版の集合はここ 1 ヶ所**。
        ///
        /// なぜ門にするのか: 将来 item_shards の意味やチャンク配置が変わったフィードを
        /// 古いクライアントが読むと、該当 shard を見つけられず「未収載」と**断言**する。
        /// 収載されている商品を「載っていない」と言い切るのは、この製品が最も避けるべき
        /// 壊れ方。
        /// 読めない書式は読めないと言う方が、正しく間違えられる。
        /// </summary>
        private static readonly string[] SupportedSchemas = { "0.1" };

        public static IReadOnlyList<string> SupportedFeedSchemaVersions => SupportedSchemas;

        public static bool IsSupportedFeedSchemaVersion(string version)
        {
            if (version == null) return false;
            for (int i = 0; i < SupportedSchemas.Length; i++)
                if (string.Equals(SupportedSchemas[i], version, StringComparison.Ordinal)) return true;
            return false;
        }

        private readonly byte[] _rawBytes;
        private readonly byte[] _signature;
        private readonly List<FeedChunkEntry> _chunks;
        private readonly Dictionary<string, FeedChunkEntry> _byPath;
        private readonly List<string> _recordSchemaVersions;

        private FeedIndex(byte[] rawBytes, byte[] signature, List<FeedChunkEntry> chunks,
                          Dictionary<string, FeedChunkEntry> byPath,
                          string dataAsOf, string feedSchemaVersion, int itemShards,
                          string signingKeyId, List<string> recordSchemaVersions,
                          long itemCount, long termsCount)
        {
            _rawBytes = rawBytes;
            _signature = signature;
            _chunks = chunks;
            _byPath = byPath;
            _recordSchemaVersions = recordSchemaVersions;
            DataAsOf = dataAsOf;
            FeedSchemaVersion = feedSchemaVersion;
            ItemShards = itemShards;
            SigningKeyId = signingKeyId;
            ItemCount = itemCount;
            TermsCount = termsCount;
        }

        public IReadOnlyList<FeedChunkEntry> Chunks => _chunks;
        public string DataAsOf { get; }
        public string FeedSchemaVersion { get; }
        public int ItemShards { get; }
        public string SigningKeyId { get; }
        public IReadOnlyList<string> RecordSchemaVersions => _recordSchemaVersions;
        public long ItemCount { get; }
        public long TermsCount { get; }

        /// <summary>署名が掛かっている当のバイト列。**複製を返す** (外から書き換えられたら証拠でなくなる)。</summary>
        public byte[] RawBytes
        {
            get
            {
                var copy = new byte[_rawBytes.Length];
                Buffer.BlockCopy(_rawBytes, 0, copy, 0, _rawBytes.Length);
                return copy;
            }
        }

        internal byte[] RawBytesReference => _rawBytes; // 内部での書き出し用 (複製を挟まない)

        /// <summary>
        /// この index に対して検証が通った署名。**複製を返す**。
        ///
        /// 署名を index の内側に持たせているのは、「検証済み index」と「その署名」が別々の
        /// 裸の byte[] として持ち回られると、対応しない組を作れてしまうため (実測: v1 の
        /// index と v2 の署名でキャッシュが書け、以後ロードが毎回失敗して回復不能になった)。
        /// 型として構成不能にする方が、書き込み時の検査を足すより強い。
        /// </summary>
        public byte[] Signature
        {
            get
            {
                var copy = new byte[_signature.Length];
                Buffer.BlockCopy(_signature, 0, copy, 0, _signature.Length);
                return copy;
            }
        }

        internal byte[] SignatureReference => _signature;

        public bool TryGetChunk(string path, out FeedChunkEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(path)) return false;
            return _byPath.TryGetValue(path, out entry);
        }

        /// <summary>
        /// **署名検証を通したバイト列と、その署名だけを渡すこと**。internal なのは、その順序を
        /// 型で守るため (FeedVerifier.VerifyIndex 以外から呼ばない)。
        ///
        /// 未知のメンバ (license など) は無視する — record_schema_versions が上がった時に
        /// 古いパッケージが全滅しないため。ただし**フィード書式そのもの**の互換性は
        /// feed_schema_version で明示的に門を掛ける (無視すると「未収載」と誤答する)。
        /// </summary>
        internal static FeedIndexParse TryParse(byte[] verifiedBytes, byte[] verifiedSignature,
                                                out FeedIndex index, out string error)
        {
            index = null;
            error = null;

            if (verifiedSignature == null || verifiedSignature.Length != FeedVerifier.SignatureLength)
            {
                error = "検証済み署名が無い (呼び出し順序の誤り)";
                return FeedIndexParse.Malformed;
            }

            JsonValue root;
            string parseError;
            if (!Json.TryParse(verifiedBytes, out root, out parseError))
            {
                error = "index を JSON として読めない: " + parseError;
                return FeedIndexParse.Malformed;
            }
            if (root.Kind != JsonKind.Object)
            {
                error = "index のトップレベルがオブジェクトでない";
                return FeedIndexParse.Malformed;
            }

            string feedSchemaVersion, dataAsOf, signingKeyId;
            if (!root.TryGetString("feed_schema_version", out feedSchemaVersion))
            {
                error = "index に feed_schema_version が無い";
                return FeedIndexParse.Malformed;
            }
            if (!IsSupportedFeedSchemaVersion(feedSchemaVersion))
            {
                // 他の検査より先に落とす: 書式が違えば「壊れている」ように見えるのは当たり前で、
                // 利用者に要る情報は「パッケージが古い」の方。
                error = "このフィードの書式 (feed_schema_version=" + FeedText.Sanitize(feedSchemaVersion, 32) +
                        ") はこのパッケージが対応していない (対応: " +
                        string.Join(" / ", SupportedSchemas) + ")。パッケージを更新してください";
                return FeedIndexParse.UnsupportedSchema;
            }
            if (!root.TryGetString("data_as_of", out dataAsOf))
            {
                error = "index に data_as_of が無い";
                return FeedIndexParse.Malformed;
            }
            if (!root.TryGetString("signing_key_id", out signingKeyId) || signingKeyId.Length == 0)
            {
                error = "index に signing_key_id が無い";
                return FeedIndexParse.Malformed;
            }

            long itemShardsLong;
            if (!root.TryGetInt64("item_shards", out itemShardsLong) ||
                itemShardsLong <= 0 || itemShardsLong > int.MaxValue)
            {
                // 0 だと shard 計算がゼロ除算になる。負値・巨大値も同様に意味を持たない。
                error = "index の item_shards が正の整数でない";
                return FeedIndexParse.Malformed;
            }
            int itemShards = (int)itemShardsLong;

            JsonValue counts;
            long itemCount, termsCount;
            if (!root.TryGetObject("counts", out counts) ||
                !counts.TryGetInt64("items", out itemCount) ||
                !counts.TryGetInt64("terms", out termsCount) ||
                itemCount < 0 || termsCount < 0)
            {
                error = "index の counts (items / terms) が読めない";
                return FeedIndexParse.Malformed;
            }

            var recordSchemaVersions = new List<string>();
            JsonValue versions;
            if (!root.TryGetArray("record_schema_versions", out versions))
            {
                error = "index に record_schema_versions が無い";
                return FeedIndexParse.Malformed;
            }
            for (int i = 0; i < versions.Items.Count; i++)
            {
                if (versions.Items[i].Kind != JsonKind.String)
                {
                    error = "record_schema_versions[" + i + "] が文字列でない";
                    return FeedIndexParse.Malformed;
                }
                recordSchemaVersions.Add(versions.Items[i].Text);
            }

            JsonValue chunksArray;
            if (!root.TryGetArray("chunks", out chunksArray))
            {
                error = "index に chunks 配列が無い";
                return FeedIndexParse.Malformed;
            }

            var chunks = new List<FeedChunkEntry>();
            var byPath = new Dictionary<string, FeedChunkEntry>(StringComparer.Ordinal);
            for (int i = 0; i < chunksArray.Items.Count; i++)
            {
                JsonValue c = chunksArray.Items[i];
                string path, sha256;
                long bytes;
                if (c.Kind != JsonKind.Object ||
                    !c.TryGetString("path", out path) ||
                    !c.TryGetString("sha256", out sha256) ||
                    !c.TryGetInt64("bytes", out bytes))
                {
                    error = "chunks[" + i + "] の形が不正 (path / sha256 / bytes)";
                    return FeedIndexParse.Malformed;
                }

                if (!FeedPath.IsValidChunkPath(path))
                {
                    // **index に不正なパスが 1 つでもあれば index 全体を拒否する**。
                    // 「その行だけ捨てて残りを使う」にすると、攻撃者は 1 行の細工で
                    // 「一部だけ欠けたフィード」を作れてしまい、欠落が正常系に見える。
                    error = "chunks[" + i + "] の path が受け入れ可能な形でない: " + path;
                    return FeedIndexParse.Malformed;
                }
                if (!sha256.StartsWith(Sha256Prefix, StringComparison.Ordinal) ||
                    !FeedHex.IsLowerHex(sha256.Substring(Sha256Prefix.Length), Sha256HexLength))
                {
                    // 表記を 1 つに固定する (大文字混じりを許すと「一致するのに一致しない」が起きる)。
                    error = "chunks[" + i + "] の sha256 が \"sha256:\" + 小文字 16 進 64 桁でない: " + sha256;
                    return FeedIndexParse.Malformed;
                }
                if (bytes < 0)
                {
                    error = "chunks[" + i + "] の bytes が負";
                    return FeedIndexParse.Malformed;
                }

                var entry = new FeedChunkEntry(path, sha256, bytes);
                if (byPath.ContainsKey(path))
                {
                    // 同じパスに 2 つの hash があると、どちらで照合しても「正しい」が作れる。
                    error = "chunks に同じ path が 2 度現れる: " + path;
                    return FeedIndexParse.Malformed;
                }
                byPath.Add(path, entry);
                chunks.Add(entry);
            }

            var raw = new byte[verifiedBytes.Length];
            Buffer.BlockCopy(verifiedBytes, 0, raw, 0, verifiedBytes.Length);

            var signature = new byte[verifiedSignature.Length];
            Buffer.BlockCopy(verifiedSignature, 0, signature, 0, verifiedSignature.Length);

            index = new FeedIndex(raw, signature, chunks, byPath, dataAsOf, feedSchemaVersion, itemShards,
                                  signingKeyId, recordSchemaVersions, itemCount, termsCount);
            return FeedIndexParse.Ok;
        }
    }
}
