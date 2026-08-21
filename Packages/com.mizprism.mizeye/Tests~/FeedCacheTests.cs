// ローカルキャッシュの往復と、壊れたキャッシュを信じないことのテスト。
//
// キャッシュの安全性は「書き込みが原子的だから」ではなく「**読むたびに再検証するから**」
// 得られている。したがってここで測るのは (a) 往復でバイト列が 1 バイトも変わらないこと、
// (b) 書いた後に壊されたキャッシュを使わないこと、の 2 点。
//
// これに (c) **壊れた 1 枚のために前回キャッシュを全損させないこと** が加わる (内容アドレスの
// レイアウト)。汚染データを表示しないだけでは半分で、「前回キャッシュを維持」
// も同じ条文の約束だった — 実測では書き込み中断で前の世代がまるごと消えていた。

using System;
using System.Collections.Generic;
using System.IO;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedCacheTests
    {
        private static readonly DateTimeOffset FetchedAt = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        private static Dictionary<string, byte[]> ChunkBodies(string generation, FeedIndex index)
        {
            var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < index.Chunks.Count; i++)
                bodies[index.Chunks[i].Path] = FeedFixtures.ChunkBytes(generation, index.Chunks[i].Path);
            return bodies;
        }

        [Fact]
        public void RoundTripsBytesExactly()
        {
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                byte[] signature = FeedFixtures.Bytes("v1", "index.json.sig");
                Dictionary<string, byte[]> bodies = ChunkBodies("v1", index);

                string error;
                Assert.True(cache.TryStore(index, bodies, FetchedAt, out error), error);

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);

                // バイト列が完全一致すること (テキストとして読み書きしていたらここで落ちる)
                Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), cached.Index.RawBytes);
                Assert.Equal(signature, cached.Signature);
                Assert.Empty(cached.MissingChunks);
                foreach (KeyValuePair<string, byte[]> pair in bodies)
                {
                    byte[] body;
                    Assert.True(cached.TryGetChunk(pair.Key, out body));
                    Assert.Equal(pair.Value, body);
                }

                // 最終取得時刻が ISO-8601 UTC で往復すること
                Assert.Equal(FetchedAt, cached.LastSuccessUtc);

                // 再検証も通ること (ロードは毎回検証を通る経路であることの確認)
                FeedIndex reverified;
                Assert.True(FeedVerifier.VerifyIndex(cached.Index.RawBytes, cached.Signature,
                    FeedFixtures.Keyring(), out reverified).IsOk);
            }
        }

        [Fact]
        public void RejectsCacheWithTamperedIndex()
        {
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), FetchedAt, out error), error);

                // 書いた後にキャッシュを 1 バイト壊す (torn write / 外からの書き換えの模擬)
                string indexPath = Path.Combine(temp.Root, "index.json");
                byte[] onDisk = File.ReadAllBytes(indexPath);
                File.WriteAllBytes(indexPath, FeedFixtures.FlipByte(onDisk, onDisk.Length / 2));

                CachedFeed cached;
                string reason;
                Assert.False(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason));
                Assert.Null(cached);
                Assert.Contains("再検証できない", reason);
            }
        }

        [Fact]
        public void TamperedChunkIsDroppedWithoutLosingTheCache()
        {
            // **期待値を入れ替えたテスト** (旧: RejectsCacheWithTamperedChunk = キャッシュ全体を捨てる)。
            // 内容アドレスでは、ファイル名が内容の hash なので「その名前のファイル」は一致するか
            // 壊れているかのどちらかしかない — 前の世代の別の本文がそこに残ることは起こらない。
            // 壊れた 1 枚は汚染ではなく事故なので、その 1 枚だけを欠落として諦める (// 「前回キャッシュを維持」side)。汚染データを見せないこと自体は下の Assert で測る。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), FetchedAt, out error), error);

                string chunkFile = FeedFixtures.CachedChunkFile(temp.Root, index, "terms/11111111.json");
                byte[] onDisk = File.ReadAllBytes(chunkFile);
                File.WriteAllBytes(chunkFile, FeedFixtures.FlipByte(onDisk, 5));

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);

                byte[] body;
                Assert.Equal(new[] { "terms/11111111.json" }, cached.MissingChunks);
                Assert.False(cached.TryGetChunk("terms/11111111.json", out body)); // 壊れた本文は出てこない
                Assert.True(cached.TryGetChunk("terms/22222222.json", out body));  // 残りは無事
                Assert.Equal(FeedFixtures.ChunkBytes("v1", "terms/22222222.json"), body);

                // 壊れた 1 枚は消えている (次の取得で埋め直せる状態に戻す)
                Assert.False(File.Exists(chunkFile));
            }
        }

        [Fact]
        public void TornWriteOfARevisedChunkKeepsThePreviousGeneration()
        {
            // **欠陥 (1) の回帰**: 改訂チャンクを書いた直後にプロセスが落ちた状態 (= 新しい本文が
            // ディスクにあり、index はまだ前の世代) を作る。旧レイアウトではこの状態で TryLoad が
            // 不一致を汚染とみなし、キャッシュ**全体**を捨てていた (ディスク満杯や AV ロックで
            // TryStore が途中失敗した時も同じ)。内容アドレスでは新しい本文は別ファイルとして
            // 増えるだけなので、前の世代の index は全チャンクを満たしたまま残る。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex v1 = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(v1, ChunkBodies("v1", v1), FetchedAt, out error), error);

                // 中断の再現: 次の世代のチャンクだけを書き、index / 署名 / meta は commit しない。
                FeedIndex v2 = FeedFixtures.VerifiedIndex("v2");
                var revised = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    { "terms/11111111.json", FeedFixtures.ChunkBytes("v2", "terms/11111111.json") },
                    { "items/02.json", FeedFixtures.ChunkBytes("v2", "items/02.json") }
                };
                Assert.True(cache.TryStoreChunks(v2, revised, out error), error);

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);

                // 前の世代がそのまま使える (1 枚も欠けていない)
                Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), cached.Index.RawBytes);
                Assert.Empty(cached.MissingChunks);
                Assert.Equal(FetchedAt, cached.LastSuccessUtc);
                foreach (KeyValuePair<string, byte[]> pair in ChunkBodies("v1", v1))
                {
                    byte[] body;
                    Assert.True(cached.TryGetChunk(pair.Key, out body));
                    Assert.Equal(pair.Value, body); // 改訂前の本文が読める (新しい本文が混ざらない)
                }
            }
        }

        [Fact]
        public void AStoreThatDiesBeforeCommittingKeepsEveryChunkOfThePreviousGeneration()
        {
            // 上のテストは中断を TryStoreChunks で**模して**いるが、実際の中断は TryStore の
            // 途中で起きる — そして TryStore は片付け (PruneChunks) も持っている。片付けが
            // index を commit する**前**に走ると、新しい index が参照しない本文 = 前の世代で
            // 改訂されたチャンクが先に消え、まだ差し替わっていない古い index がそれを失う。
            //
            // つまり「前回キャッシュが生き残る」は書き込み順に依存している。順序を入れ替えても
            // 他のテストは全て緑のままだった (実測) ので、ここで本物の中断を起こして固定する。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex v1 = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(v1, ChunkBodies("v1", v1), FetchedAt, out error), error);

                // commit の最初の一歩 (署名の書き込み) を失敗させる。書き込みは <path>.tmp を
                // 経由するので、そこをディレクトリにしておくと書けない = 例外で中断する。
                // 署名で止めるのは、index と署名が**揃って前の世代のまま**残るのがこの検査の
                // 前提だから (片方だけ新しくなる窓は別の問題として冒頭コメントに書いてある)。
                Directory.CreateDirectory(Path.Combine(temp.Root, "index.json.sig.tmp"));

                FeedIndex v2 = FeedFixtures.VerifiedIndex("v2");
                Assert.False(cache.TryStore(v2, ChunkBodies("v2", v2), FetchedAt.AddHours(25), out error));
                Assert.NotNull(error);

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);

                // 前の世代が**完全な形で**残っている。1 枚でも欠けていたら、片付けが早すぎる。
                Assert.Equal(FeedFixtures.Bytes("v1", "index.json"), cached.Index.RawBytes);
                Assert.Empty(cached.MissingChunks);
                foreach (KeyValuePair<string, byte[]> pair in ChunkBodies("v1", v1))
                {
                    byte[] body;
                    Assert.True(cached.TryGetChunk(pair.Key, out body), pair.Key + " が消えている");
                    Assert.Equal(pair.Value, body);
                }
            }
        }

        [Fact]
        public void StoringANewGenerationRemovesFilesTheIndexNoLongerReferences()
        {
            // 前の世代の本文と旧レイアウト (chunks/items/00.json) の残骸は、新しい index を
            // commit した**後**に片付ける。片付けが commit の後なので、中断しても前の世代は残る。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex v1 = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(v1, ChunkBodies("v1", v1), FetchedAt, out error), error);

                // 旧レイアウトのキャッシュを手元に持っている利用者を模す
                string legacy = Path.Combine(temp.Root, "chunks", "items", "00.json");
                Directory.CreateDirectory(Path.GetDirectoryName(legacy));
                File.WriteAllBytes(legacy, FeedFixtures.ChunkBytes("v1", "items/00.json"));

                string supersededTerms = FeedFixtures.CachedChunkFile(temp.Root, v1, "terms/11111111.json");
                Assert.True(File.Exists(supersededTerms));

                FeedIndex v2 = FeedFixtures.VerifiedIndex("v2");
                Assert.True(cache.TryStore(v2, ChunkBodies("v2", v2), FetchedAt.AddHours(25), out error), error);

                Assert.False(File.Exists(supersededTerms)); // 改訂前の terms は参照されなくなった
                Assert.False(File.Exists(legacy));          // 旧レイアウトの残骸も消える

                // 残っているのは v2 が参照する 5 枚だけ (キャッシュが太り続けない)
                string[] files = Directory.GetFiles(Path.Combine(temp.Root, "chunks"), "*",
                                                    SearchOption.AllDirectories);
                Assert.Equal(v2.Chunks.Count, files.Length);

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Empty(cached.MissingChunks);
            }
        }

        [Fact]
        public void StoringTheSameChunkAgainDoesNotRewriteTheFile()
        {
            // 名前が内容の hash なので、既にあるファイルは同じ内容。上書きは delete → move の順で
            // 行われる = 一瞬そのチャンクがディスクから消えるので、要らない上書きはしない
            // (自分で torn window を作らない)。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), FetchedAt, out error), error);

                string chunkFile = FeedFixtures.CachedChunkFile(temp.Root, index, "items/00.json");
                var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(chunkFile, stamp);

                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), FetchedAt.AddHours(25), out error), error);

                Assert.Equal(stamp, File.GetLastWriteTimeUtc(chunkFile));
            }
        }

        [Fact]
        public void MissingChunkIsNotCorruption()
        {
            // index にあってローカルに無いチャンクは「取れていない 1 枚」。キャッシュ全体を
            // 捨てる理由にはしない。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                Dictionary<string, byte[]> bodies = ChunkBodies("v1", index);
                bodies.Remove("terms/22222222.json");

                string error;
                Assert.True(cache.TryStore(index, bodies, FetchedAt, out error), error);

                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Equal(new[] { "terms/22222222.json" }, cached.MissingChunks);
            }
        }

        [Fact]
        public void RefusesToStoreChunksThatDoNotMatchTheIndex()
        {
            // キャッシュに「index と食い違うもの」を置いた瞬間、次回のロードはキャッシュ全体を
            // 捨てる = 静かなデータ喪失になる。書く前に止める。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                Dictionary<string, byte[]> bodies = ChunkBodies("v1", index);
                bodies["items/00.json"] = FeedFixtures.FlipByte(bodies["items/00.json"], 3);

                string error;
                Assert.False(cache.TryStore(index, bodies, FetchedAt, out error));
                Assert.Contains("検証を通らないチャンクは書かない", error);
            }
        }

        [Fact]
        public void RejectsNullChunkBodies()
        {
            // null は誤用 (何を指示されたのか判らないまま書き込みを始めない) として弾く。
            // **期待値を入れ替えた箇所**: 空辞書はかつて「キャッシュを空にする」指示だった
            // (PruneChunks が index にあるのに今回書かなかったものも消していたため)。内容アドレスでは
            // 手元の本文がそのまま新しい index を満たすので、空辞書は「index だけ差し替え」になる。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                string error;
                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), FetchedAt, out error), error);

                Assert.False(cache.TryStore(index, null, FetchedAt.AddHours(25), out error));
                Assert.Contains("チャンクが渡されていない", error);

                // 元のチャンクが残っていること
                CachedFeed cached;
                string reason;
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Empty(cached.MissingChunks);

                // 空辞書 = index だけ差し替え。手元の本文は同じ index を満たすので消えない
                Assert.True(cache.TryStore(index, new Dictionary<string, byte[]>(StringComparer.Ordinal),
                    FetchedAt.AddHours(25), out error), error);
                Assert.True(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason), reason);
                Assert.Empty(cached.MissingChunks);
            }
        }

        [Fact]
        public void RecordsAttemptAndSuccessSeparately()
        {
            // 24 時間の上限は「成功」で数え、連打の門は「試行」で数える。1 つの時刻では表せない。
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                string error;

                Assert.True(cache.TryRecordAttempt(FetchedAt, out error), error);
                Assert.Equal(FetchedAt, cache.ReadMeta().LastAttemptUtc);
                Assert.Null(cache.ReadMeta().LastSuccessUtc); // 失敗した取得は成功時刻を進めない

                FeedIndex index = FeedFixtures.VerifiedIndex("v1");
                DateTimeOffset success = FetchedAt.AddMinutes(1);
                Assert.True(cache.TryStore(index, ChunkBodies("v1", index), success, out error), error);
                Assert.Equal(success, cache.ReadMeta().LastSuccessUtc);
                Assert.Equal(success, cache.ReadMeta().LastAttemptUtc);

                // 成功のあとの失敗試行でも、成功時刻は保たれること
                Assert.True(cache.TryRecordAttempt(success.AddHours(30), out error), error);
                Assert.Equal(success, cache.ReadMeta().LastSuccessUtc);
                Assert.Equal(success.AddHours(30), cache.ReadMeta().LastAttemptUtc);
            }
        }

        [Fact]
        public void ReportsNoCacheWhenNothingWasStored()
        {
            using (var temp = new TempDirectory())
            {
                var cache = new FeedCache(temp.Root);
                CachedFeed cached;
                string reason;
                Assert.False(cache.TryLoad(FeedFixtures.Keyring(), out cached, out reason));
                Assert.Null(cache.ReadMeta().LastSuccessUtc);
            }
        }
    }
}
