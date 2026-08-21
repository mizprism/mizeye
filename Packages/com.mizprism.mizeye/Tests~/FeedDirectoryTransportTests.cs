// ローカルディレクトリ取得口のテスト。
//
// 掴むべき欠陥は 2 つで、どちらも「動いているように見えて壊れている」型:
//
//  1. **ディレクトリの外に出る** — 相対パスはフィード由来の外来文字列で、そのまま
//     ローカルのファイルパスになる。ネットワーク実装なら 404 で済むが、こちらは
//     読めてしまう。通ったことはテストしないと判らない (読めた側は正常に見える)。
//  2. **例外を投げる** — IFeedTransport の契約は「失敗を Unreachable で返す」。
//     例外にすると縮退系が呼び出し側の catch 漏れで壊れるが、
//     正常系のテストだけ書いていると気付かない。
//
// 実データ (dist/feed) には依存させない。dist/ は .gitignore 済みで CI には存在せず、
// そこに依存すると **CI では常にスキップされて緑**になる (最も静かな未検証)。

using System;
using System.IO;
using System.Text;
using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedDirectoryTransportTests
    {
        private sealed class Feed : IDisposable
        {
            internal Feed()
            {
                Temp = new TempDirectory();
                Root = Path.Combine(Temp.Root, "feed");
                Directory.CreateDirectory(Path.Combine(Root, "items"));
                Directory.CreateDirectory(Path.Combine(Root, "terms"));
                Write("index.json", "{\"chunks\":[]}");
                Write("index.json.sig", "signature-bytes");
                Write(Path.Combine("items", "00.json"), "{\"items\":[]}");
                Write(Path.Combine("terms", "18de8a92.json"), "{\"terms_id\":\"sha256:18de8a92\"}");

                // フィードの外に置いた「読めてはいけない」ファイル
                Outside = Path.Combine(Temp.Root, "secret.json");
                File.WriteAllBytes(Outside, new UTF8Encoding(false, true).GetBytes("{\"secret\":true}"));
            }

            internal TempDirectory Temp { get; }
            internal string Root { get; }
            internal string Outside { get; }

            private void Write(string relative, string body) =>
                File.WriteAllBytes(Path.Combine(Root, relative),
                                   new UTF8Encoding(false, true).GetBytes(body));

            public void Dispose() => Temp.Dispose();
        }

        [Fact]
        public void ItServesTheIndexTheSignatureAndChunks()
        {
            using (var f = new Feed())
            {
                var transport = new FeedDirectoryTransport(f.Root);

                FeedTransportResult index = transport.Get("index.json");
                Assert.True(index.Reachable);
                Assert.NotEmpty(index.Body);

                Assert.True(transport.Get("index.json.sig").Reachable);
                Assert.True(transport.Get("items/00.json").Reachable);
                Assert.True(transport.Get("terms/18de8a92.json").Reachable);
            }
        }

        [Fact]
        public void ItCannotBeWalkedOutOfItsRoot()
        {
            // 相対パスはフィード由来 = 外来文字列。ここが通ると、壊れた (あるいは細工された)
            // index が指した任意のファイルをローカルから読み出せる。
            using (var f = new Feed())
            {
                var transport = new FeedDirectoryTransport(f.Root);

                // 前提の確認: 出てはいけない先に実在のファイルがある (的が空だと素通りが見えない)
                Assert.True(File.Exists(f.Outside));

                string[] escapes =
                {
                    "../secret.json",
                    "items/../../secret.json",
                    "..\\secret.json",
                    "/etc/passwd",
                    "C:\\Windows\\win.ini",
                    "items/00.json/../../../secret.json",
                    "terms/..%2f..%2fsecret.json"
                };

                foreach (string escape in escapes)
                {
                    FeedTransportResult result = transport.Get(escape);
                    Assert.False(result.Reachable, "ルートの外に出た: " + escape);
                    Assert.NotNull(result.Error);
                }
            }
        }

        [Fact]
        public void ItRejectsPathsTheFeedFormatDoesNotDefine()
        {
            // 判定は FeedPath.IsValidChunkPath に委ねている。ここが素通りすると
            // allowlist が denylist に退化する (「危ないものを弾く」に変わる)。
            using (var f = new Feed())
            {
                var transport = new FeedDirectoryTransport(f.Root);

                Assert.False(transport.Get("items/00.JSON").Reachable);  // 大文字は別物
                Assert.False(transport.Get("items/zz.json").Reachable);  // 16 進でない
                Assert.False(transport.Get("other/00.json").Reachable);  // 未定義のディレクトリ
                Assert.False(transport.Get("index.json.bak").Reachable);
                Assert.False(transport.Get("").Reachable);
                Assert.False(transport.Get(null).Reachable);
            }
        }

        [Fact]
        public void AMissingFileIsUnreachableRatherThanAnException()
        {
            // 縮退系は正常系。例外にすると呼び出し側の catch 漏れが利用者に見える。
            using (var f = new Feed())
            {
                var transport = new FeedDirectoryTransport(f.Root);

                FeedTransportResult missing = transport.Get("terms/deadbeef.json");
                Assert.False(missing.Reachable);
                Assert.Contains("404", missing.Error);
            }
        }

        [Fact]
        public void AMissingRootIsUnreachableRatherThanAnException()
        {
            var transport = new FeedDirectoryTransport(Path.Combine(Path.GetTempPath(), "mizprism-no-such-feed"));
            FeedTransportResult result = transport.Get("index.json");
            Assert.False(result.Reachable);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public void ItReturnsTheBytesUnchanged()
        {
            // テキストとして読むと改行変換や BOM が混ざり、署名対象が壊れる。
            using (var f = new Feed())
            {
                string path = Path.Combine(f.Root, "index.json");
                byte[] onDisk = File.ReadAllBytes(path);

                FeedTransportResult served = new FeedDirectoryTransport(f.Root).Get("index.json");
                Assert.True(served.Reachable);
                Assert.Equal(onDisk, served.Body);
            }
        }

        [Fact]
        public void ItDrivesTheRealClientEndToEnd()
        {
            // 取得口の単体挙動だけでなく、**FeedClient に食わせて署名検証まで通るか**。
            // 署名済み fixture (feed-terms) をディレクトリとして配信する。
            using (var temp = new TempDirectory())
            {
                string root = Path.Combine(FeedFixtures.TermsFixtureDir(), "v1");
                var client = new FeedClient(new FeedDirectoryTransport(root), new FeedCache(temp.Root),
                                            FeedFixtures.TermsKeyring(), SystemClock.Instance);

                FeedRefreshResult result = client.Refresh(manual: false);
                Assert.Equal(FeedRefreshOutcome.Updated, result.Outcome);
                Assert.Empty(result.RejectedChunks);

                FeedItem item;
                string reason;
                Assert.Equal(FeedItemLookup.Found, client.TryGetItem(3741802, out item, out reason));

                FeedTerms terms;
                Assert.Equal(FeedItemLookup.Found, client.TryGetTerms(item.TermsRef, out terms, out reason));
                Assert.Equal("RadDollV3利用規約", terms.Title);
            }
        }
    }
}
