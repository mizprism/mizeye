// データ層テストの共通部品 (fixture の場所・偽の transport / 時計・使い捨てディレクトリ)。
//
// fixture は tools/fixtures/feed-cache/。**鍵は使い捨てで秘密鍵は破棄済み**なので、
// 新しい署名を作ることはできない — 改竄ケースは既存バイト列を壊す方向でしか書けない。
// これは制約ではなく、テストが本物の署名を測っている証拠でもある。

using System;
using System.Collections.Generic;
using System.IO;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    internal static class FeedFixtures
    {
        /// <summary>
        /// 版管理された目印 (<c>.mizeye-repo-root</c>) を上に辿ってリポジトリルートを決める
        /// (Ed25519VerifierTests と同じ規約)。
        ///
        /// **版管理されないファイルを目印にしない** — 以前は開発者向けのメモファイルを
        /// 探していたが、それは追跡対象外なので、開発機では緑・clone 直後は fixture 系が
        /// 全滅、という見えにくい壊れ方をする。
        /// </summary>
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, RootMarker)))
                dir = dir.Parent;
            Assert.NotNull(dir); // リポジトリルートが見つからないなら fixture も引けない
            return dir.FullName;
        }

        /// <summary>リポジトリルートの目印 (版管理されていること)。</summary>
        internal const string RootMarker = ".mizeye-repo-root";

        /// <summary>
        /// パッケージ本体のディレクトリ。**構造ガード (ソースを読んで禁止トークンを探す系) が
        /// ここを引く** ので、置き場所は 1 箇所に持つ — 移設前のパスが残っていたせいで
        /// ガードだけが存在しないディレクトリを見ていた (存在確認で赤くなり発覚)。
        /// </summary>
        internal static string PackageDir(params string[] parts)
        {
            string path = Path.Combine(RepoRoot(), "Packages", "com.mizprism.mizeye");
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            return path;
        }

        internal static string CacheFixtureDir(params string[] parts)
        {
            string path = Path.Combine(RepoRoot(), "tools", "fixtures", "feed-cache");
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            return path;
        }

        /// <summary>fixture を**バイト列のまま**読む (テスト側でも改行変換を挟まない)。</summary>
        internal static byte[] Bytes(params string[] parts) => File.ReadAllBytes(CacheFixtureDir(parts));

        /// <summary>フィード相対パス ("items/00.json") のチャンクを世代指定で読む。</summary>
        internal static byte[] ChunkBytes(string generation, string chunkPath)
        {
            string[] parts = chunkPath.Split(new[] { '/' });
            var all = new string[parts.Length + 1];
            all[0] = generation;
            System.Array.Copy(parts, 0, all, 1, parts.Length);
            return Bytes(all);
        }

        /// <summary>
        /// シードの terms レコード (実物) を**バイト列のまま**読む。
        ///
        /// **凍結スナップショット**であって生きたコーパスではない (tools/fixtures/seed-terms/README.md)。
        /// コーパス全件を舐める適合テスト — 新しいレコードが出た時に赤くなるもの — は、
        /// コーパスが変わる側が持つ。ここに凍結コピーを置いて全件テストを書くと、
        /// **赤くなるべき事由が構造的に発生しない**テストになる。
        /// </summary>
        internal static byte[] SeedTermsBytes(string stem) =>
            File.ReadAllBytes(Path.Combine(RepoRoot(), "tools", "fixtures", "seed-terms", stem + ".json"));

        /// <summary>
        /// 2 本目の fixture (tools/fixtures/feed-terms)。**規約 1 本を引き当てる経路を
        /// end-to-end で測るためだけ**にあり、中身は実レコード (RadDollV3 の規約と、それを
        /// 参照する item) を配信形にしたもの。
        ///
        /// feed-cache と分けたのは、あちらの秘密鍵が破棄済みで新しい署名を足せないため —
        /// 既存の期待値を一切動かさずに世代を増やす手段が「別 fixture」しかない。
        /// こちらの鍵も使い捨てで、署名を検証した直後に破棄している。
        /// </summary>
        internal static string TermsFixtureDir(params string[] parts)
        {
            string path = Path.Combine(RepoRoot(), "tools", "fixtures", "feed-terms");
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            return path;
        }

        internal static FeedKeyring TermsKeyring()
        {
            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyring.TryParse(
                File.ReadAllBytes(TermsFixtureDir("keyring.json")), out keyring, out error), error);
            return keyring;
        }

        internal static FeedKeyring Keyring()
        {
            FeedKeyring keyring;
            string error;
            Assert.True(FeedKeyring.TryParse(Bytes("keyring.json"), out keyring, out error), error);
            return keyring;
        }

        /// <summary>fixture の世代 (v1 / v2) を検証して index を得る。</summary>
        internal static FeedIndex VerifiedIndex(string generation)
        {
            FeedIndex index;
            FeedVerifyResult result = FeedVerifier.VerifyIndex(
                Bytes(generation, "index.json"), Bytes(generation, "index.json.sig"), Keyring(), out index);
            Assert.True(result.IsOk, result.ToString());
            return index;
        }

        /// <summary>
        /// キャッシュ上のチャンクファイルのパス (レイアウトは内容アドレス = chunks/&lt;sha256 hex&gt;.json)。
        ///
        /// **FeedCache の private ヘルパを使わず、index の sha256 から独立に組み立てる** — 実装と
        /// 同じ関数で場所を決めると、レイアウトが変わってもテストが一緒に動いてしまい、
        /// 「どこに置いたか」を測れなくなる。
        /// </summary>
        internal static string CachedChunkFile(string cacheRoot, FeedIndex index, string chunkPath)
        {
            FeedChunkEntry entry;
            Assert.True(index.TryGetChunk(chunkPath, out entry), "index に無いチャンク: " + chunkPath);
            const string prefix = "sha256:";
            Assert.StartsWith(prefix, entry.Sha256);
            return Path.Combine(cacheRoot, "chunks", entry.Sha256.Substring(prefix.Length) + ".json");
        }

        /// <summary>1 バイトだけ反転した複製 (改竄ケース)。</summary>
        internal static byte[] FlipByte(byte[] bytes, int offset)
        {
            var copy = (byte[])bytes.Clone();
            copy[offset] ^= 0x01;
            return copy;
        }
    }

    /// <summary>時刻を手で動かせる時計。24 時間の境界をテストで踏むために要る。</summary>
    internal sealed class FakeClock : IClock
    {
        internal FakeClock(DateTimeOffset start)
        {
            UtcNow = start;
        }

        public DateTimeOffset UtcNow { get; set; }

        internal void Advance(TimeSpan delta) => UtcNow = UtcNow + delta;
    }

    /// <summary>fixture ディレクトリを配信する偽 transport。到達不可・本文の差し替えを仕込める。</summary>
    internal sealed class FakeFeedTransport : IFeedTransport
    {
        private readonly Dictionary<string, byte[]> _overrides = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly HashSet<string> _unreachable = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _fixtureRoot;
        private string _generation;

        internal FakeFeedTransport(string generation) : this(generation, null)
        {
        }

        /// <summary>fixtureRoot に null 以外を渡すと、その fixture を配信する (既定は feed-cache)。</summary>
        internal FakeFeedTransport(string generation, string fixtureRoot)
        {
            _generation = generation;
            _fixtureRoot = fixtureRoot;
        }

        /// <summary>Get が呼ばれた回数 (レート制限が本当にネットワークに触れていないかを測る)。</summary>
        internal int CallCount { get; private set; }

        internal List<string> Requested { get; } = new List<string>();

        internal bool AllUnreachable { get; set; }

        internal void UseGeneration(string generation) => _generation = generation;

        /// <summary>仕込んだ障害と呼び出し回数を消す (「回復したフィード」を作る)。</summary>
        internal void Reset()
        {
            _overrides.Clear();
            _unreachable.Clear();
            AllUnreachable = false;
            CallCount = 0;
            Requested.Clear();
        }

        /// <summary>この相対パスの応答を差し替える (転送事故・改竄の再現)。</summary>
        internal void Override(string relativePath, byte[] body) => _overrides[relativePath] = body;

        internal void MakeUnreachable(string relativePath) => _unreachable.Add(relativePath);

        public FeedTransportResult Get(string relativePath)
        {
            CallCount++;
            Requested.Add(relativePath);

            if (AllUnreachable || _unreachable.Contains(relativePath))
                return FeedTransportResult.Unreachable("到達不可 (テスト設定): " + relativePath);

            byte[] overridden;
            if (_overrides.TryGetValue(relativePath, out overridden))
                return FeedTransportResult.Ok(overridden);

            string[] parts = relativePath.Split(new[] { '/' });
            string path = _fixtureRoot == null
                ? FeedFixtures.CacheFixtureDir(_generation)
                : Path.Combine(_fixtureRoot, _generation);
            for (int i = 0; i < parts.Length; i++) path = Path.Combine(path, parts[i]);
            if (!File.Exists(path)) return FeedTransportResult.Unreachable("404: " + relativePath);
            return FeedTransportResult.Ok(File.ReadAllBytes(path));
        }
    }

    /// <summary>テスト用の使い捨てディレクトリ。</summary>
    internal sealed class TempDirectory : IDisposable
    {
        private const string Marker = "mizprism-feed-tests";

        internal TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), Marker, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        public void Dispose()
        {
            try
            {
                // 自分で作った印のあるディレクトリしか消さない。
                if (Root.Contains(Marker) && Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
