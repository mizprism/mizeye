// チャンクパス検査の直接テスト。
//
// **ここを直接測るのは、この関数がキャッシュ書き込みの唯一の関門だから**。フィード由来の
// 文字列がローカルのファイルパスになる以上、`../` や絶対パス、ドライブレターを 1 つでも
// 通したらキャッシュディレクトリの外に書ける。Windows は区切り文字が 2 種類あるぶん穴が広い。

using Mizprism.LicenseLens;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class FeedPathTests
    {
        [Theory]
        [InlineData("items/00.json")]
        [InlineData("items/0f.json")]
        [InlineData("terms/11111111.json")]
        [InlineData("terms/1111111111111111111111111111111111111111111111111111111111111111.json")] // 64 桁
        [InlineData("terms/visual-lukume-4044305.json")]  // v1.3 visual チャンク (terms/ のみ)
        [InlineData("terms/visual-abc.json")]             // slug 最短 3 文字
        public void AcceptsWellFormedPaths(string path)
        {
            Assert.True(FeedPath.IsValidChunkPath(path), "受理すべきパスを拒否した: " + path);
        }

        [Theory]
        [InlineData("../../evil.json")]
        [InlineData("items/../x.json")]
        [InlineData("items\\..\\x.json")]
        [InlineData("/etc/passwd")]
        [InlineData("C:\\x.json")]
        [InlineData("items/00.JSON")]
        [InlineData("terms/zz.json")]
        [InlineData("/items/00.json")]
        [InlineData("items//00.json")]
        [InlineData("items/0.json")]          // 1 桁は短すぎる
        [InlineData("items/00.json/../x")]
        [InlineData("other/00.json")]
        [InlineData("items/00.json\0")]
        [InlineData("items/00 .json")]
        [InlineData("terms/1111111111111111111111111111111111111111111111111111111111111111")] // 拡張子なし
        [InlineData("terms/11111111111111111111111111111111111111111111111111111111111111111.json")] // 65 桁
        [InlineData("")]
        [InlineData(null)]
        [InlineData("items/visual-lukume-4044305.json")]  // visual は terms/ 限定 — items は hex のみ
        [InlineData("terms/visual-.json")]                // slug 空
        [InlineData("terms/visual--x.json")]              // slug 先頭が英数字でない
        [InlineData("terms/visual-ab.json")]              // slug 2 文字 (最短 3 未満)
        [InlineData("terms/visual-Lukume.json")]          // 大文字
        [InlineData("terms/visual-a/b.json")]             // 区切り文字混入
        [InlineData("terms/visual-a.b.json")]             // '.' 混入
        public void RejectsEverythingElse(string path)
        {
            Assert.False(FeedPath.IsValidChunkPath(path), "通してはいけないパスを通した: " + path);
        }

        [Fact]
        public void DerivesTermsIdFromChunkPath()
        {
            Assert.Equal("11111111", FeedPath.TermsIdFromChunkPath("terms/11111111.json"));
            Assert.Null(FeedPath.TermsIdFromChunkPath("items/00.json"));
            Assert.Null(FeedPath.TermsIdFromChunkPath("terms/../x.json"));
        }

        [Fact]
        public void BuildsChunkPathsTheSameWayAsBuildFeed()
        {
            // フィードのパス規則: terms は terms_id の先頭 8 桁、items は shard の 2 桁 16 進。
            Assert.Equal("terms/11111111.json", FeedPath.TermsChunkPath(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111"));
            Assert.Null(FeedPath.TermsChunkPath("sha256:short"));
            Assert.Null(FeedPath.TermsChunkPath("nocolon"));
            Assert.Null(FeedPath.TermsChunkPath(null));

            // v1.3 visual: 切り詰めずフル slug (8 桁に切ると slug が壊れて衝突しうる)
            Assert.Equal("terms/visual-lukume-4044305.json", FeedPath.TermsChunkPath("visual:lukume-4044305"));
            Assert.Null(FeedPath.TermsChunkPath("visual:"));            // slug 空
            Assert.Null(FeedPath.TermsChunkPath("visual:../evil"));     // 危険文字は allowlist で落ちる
            Assert.Null(FeedPath.TermsChunkPath("visual:UPPER"));       // 大文字

            Assert.Equal("items/00.json", FeedPath.ItemChunkPath(1000016, 16));
            Assert.Equal("items/01.json", FeedPath.ItemChunkPath(1000017, 16));
            Assert.Equal("items/02.json", FeedPath.ItemChunkPath(1000018, 16));
            Assert.Null(FeedPath.ItemChunkPath(-1, 16));
            Assert.Null(FeedPath.ItemChunkPath(1000016, 0)); // ゼロ除算を作らない
        }
    }
}
