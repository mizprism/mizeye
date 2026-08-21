// 配信元判定と URL 組み立てのテスト。
//
// **HTTP 取得口の実体 (UnityWebRequest) は Editor アセンブリにあり、dotnet CI は
// 1 行もコンパイルしない。** だから判断だけを Runtime の FeedSource に降ろしてある。
// ここが検証していないと、HTTP 経路の判断は「Unity で開いた人にしか判らない」状態になる。
//
// 掴むべき欠陥:
//  1. **フィード外の URL を取りに行く** — 相対パスはフィード由来の外来文字列。
//     allowlist が効いていないと、index が指した任意のパスを叩くことになる
//  2. **URL の連結が壊れる** — 末尾スラッシュの有無・クエリ付き URL は、繋いだ結果が
//     静かに別物の URL になる。404 として現れるので「フィードが壊れている」と誤診する
//  3. **allowlist が 2 実装に分裂する** — ディレクトリ側と HTTP 側で判定が食い違うと、
//     片方だけで再現する不具合になる。同じ述語を両方が使うことをここで固定する

using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedSourceTests
    {
        [Theory]
        [InlineData("index.json")]
        [InlineData("index.json.sig")]
        public void IndexPathsAreServable(string path)
        {
            Assert.True(FeedSource.IsServablePath(path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("../secrets.json")]
        [InlineData("/etc/passwd")]
        [InlineData("index.json/../../x")]
        [InlineData("terms/../../x.json")]
        [InlineData("README.md")]
        public void NonFeedPathsAreRejected(string path)
        {
            Assert.False(FeedSource.IsServablePath(path));
        }

        [Fact]
        public void ServableDelegatesToFeedPathForChunks()
        {
            // 同じ述語を 2 実装で持たないことの固定。FeedPath が認めるものは通り、
            // 認めないものは通らない — ここが食い違うと片方の取得口でだけ壊れる。
            const string chunk = "terms/abcd1234.json";
            Assert.Equal(FeedPath.IsValidChunkPath(chunk), FeedSource.IsServablePath(chunk));

            const string bogus = "terms/NOT-VALID!.json";
            Assert.Equal(FeedPath.IsValidChunkPath(bogus), FeedSource.IsServablePath(bogus));
        }

        [Theory]
        [InlineData("https://feed.mizprism.workers.dev", true)]
        [InlineData("http://localhost:8787", true)]
        [InlineData("HTTPS://FEED.EXAMPLE", true)]
        [InlineData("/Users/me/dist/feed", false)]
        [InlineData("C:\\feed", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void LooksLikeHttpUrlSeparatesUrlsFromDirectories(string source, bool expected)
        {
            Assert.Equal(expected, FeedSource.LooksLikeHttpUrl(source));
        }

        [Fact]
        public void TrailingSlashDoesNotDoubleUp()
        {
            string url;
            string error;
            Assert.True(FeedSource.TryComposeUrl("https://feed.example/", "index.json", out url, out error));
            Assert.Equal("https://feed.example/index.json", url);
            Assert.Null(error);
        }

        [Fact]
        public void ComposesChunkUrl()
        {
            string url;
            string error;
            Assert.True(FeedSource.TryComposeUrl("https://feed.example", "index.json.sig", out url, out error));
            Assert.Equal("https://feed.example/index.json.sig", url);
        }

        [Theory]
        [InlineData("https://feed.example?x=1")]
        [InlineData("https://feed.example#frag")]
        [InlineData("https://")]
        [InlineData("https:///path")]
        [InlineData("http://")]
        [InlineData("ftp://feed.example")]
        [InlineData("/Users/me/dist/feed")]
        [InlineData("")]
        public void BadRootsAreRefusedWithAReason(string root)
        {
            string url;
            string error;
            Assert.False(FeedSource.TryComposeUrl(root, "index.json", out url, out error));
            Assert.Null(url);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void UnservablePathIsRefusedEvenWithAGoodRoot()
        {
            string url;
            string error;
            Assert.False(FeedSource.TryComposeUrl("https://feed.example", "../../etc/passwd", out url, out error));
            Assert.Null(url);
            Assert.Contains("受け付けない", error);
        }
    }
}
