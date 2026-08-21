// 配布物にライセンス本文が入っていることを主張するテスト。
//
// なぜ要るか: v0.1.0 の配布 zip には LICENSE が 1 バイトも入っていなかった (2026-08-21 に
// 独立レビューが実物を展開して発見)。zip に入るのは `Packages/com.mizprism.mizeye/` 配下だけで、
// 条文はリポジトリのルートにあったため、**構造上入りようがなかった**。package.json に
// "license": "AGPL-3.0-only" と書いてあるので、主張だけが届き条文が届かない状態だった。
//
// AGPL-3.0 §4/§5 と Apache-2.0 §4(a) は、頒布する時に受領者へライセンスの写しを渡すことを
// 求めている。規約の読み方を配る製品が自分の頒布条件を守っていないのは、機能の不足ではなく
// 筋の問題である。
//
// 同じ条文を 2 箇所 (ルートと配布物) に持つので、**乖離したらここが赤くなる**ようにしてある。
// 「正本」と称するコピーは黙って腐るので、一致を機械で主張する。

using System.IO;
using Xunit;

namespace Mizprism.LicenseLens.Tests
{
    public class DistributedLicenseTests
    {
        private static string RepoRoot() => FeedFixtures.RepoRoot();

        private static string PackageDir() =>
            Path.Combine(RepoRoot(), "Packages", "com.mizprism.mizeye");

        [Theory]
        [InlineData("LICENSE.md")]
        [InlineData("THIRD-PARTY-NOTICES.md")]
        [InlineData("LICENSES/Apache-2.0.txt")]
        [InlineData("LICENSES/CC-BY-4.0.txt")]
        public void 配布されるパッケージにライセンス本文が同梱されている(string relative)
        {
            string path = Path.Combine(PackageDir(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"配布物に {relative} が無い — 頒布条件を満たさない");
            Assert.True(new FileInfo(path).Length > 500, $"{relative} が短すぎる (条文が入っていない疑い)");
        }

        [Theory]
        [InlineData("LICENSE.md", "LICENSE")]
        [InlineData("LICENSES/Apache-2.0.txt", "LICENSES/Apache-2.0.txt")]
        [InlineData("LICENSES/CC-BY-4.0.txt", "LICENSES/CC-BY-4.0.txt")]
        public void 同梱の条文はリポジトリルートの条文と同一である(string inPackage, string atRoot)
        {
            string a = Path.Combine(PackageDir(), inPackage.Replace('/', Path.DirectorySeparatorChar));
            string b = Path.Combine(RepoRoot(), atRoot.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(b), $"ルートに {atRoot} が無い");
            Assert.Equal(File.ReadAllBytes(b), File.ReadAllBytes(a));
        }

        [Fact]
        public void 同梱ファイルには_meta_がある()
        {
            // .meta が無いと build_listing.py が配布物を作れない (そこで初めて気づくのでは遅い)。
            foreach (string relative in new[]
                     {
                         "LICENSE.md", "THIRD-PARTY-NOTICES.md", "LICENSES",
                         "LICENSES/Apache-2.0.txt", "LICENSES/CC-BY-4.0.txt",
                     })
            {
                string meta = Path.Combine(
                    PackageDir(), relative.Replace('/', Path.DirectorySeparatorChar)) + ".meta";
                Assert.True(File.Exists(meta), $"{relative}.meta が無い");
            }
        }
    }
}
